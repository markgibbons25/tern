using System.Globalization;
using System.Text.Json;

using Tern.Remote.Cloud;

namespace Tern.Cli.Common;

public enum CloudFieldKind
{
    Text,

    /// <summary>A timestamp the Official CLI always prints, as <c>0001-01-01T00:00:00</c> when the API leaves it out.</summary>
    Date,
    OptionalDate,
    Number,
    Flag,

    /// <summary>A task status, written as its number (0 NotStarted … 4 Skipped) and shown as its name.</summary>
    Status,
    OptionalStatus,

    /// <summary>A task status written as its name.</summary>
    StatusName,
    Texts,
}

/// <summary>
/// One field of a SitecoreAI object: its name in the API, its name in the JSON output (which
/// follows the Official CLI's <c>--json</c> output), and the label of its row in a human table.
/// </summary>
public sealed record CloudField(string Api, string Label, CloudFieldKind Kind = CloudFieldKind.Text, bool Shown = true, string? Json = null)
{
    public string JsonName => Json ?? Api;
}

/// <summary>
/// Renders SitecoreAI objects. The JSON payloads are the Official CLI's own: the same root shape,
/// field names, casing and value types, so scripts that read <c>dotnet sitecore cloud … --json</c>
/// keep working unchanged.
/// </summary>
public static class CloudOutput
{
    public static readonly string[] TaskStatuses = ["NotStarted", "InProgress", "Complete", "Failed", "Skipped"];

    public static readonly CloudField[] Project =
    [
        new("organizationName", "Organization Name"),
        new("organizationId", "Organization Id"),
        new("id", "Project Id"),
        new("name", "Project name"),
        new("region", "Region"),
        new("createdBy", "Created by"),
        new("createdAt", "Created at", CloudFieldKind.Date),
        new("lastUpdatedBy", "Last updated by"),
        new("lastUpdatedAt", "Last updated at", CloudFieldKind.Date),
        new("sitecoreMajorVersion", "Sitecore major version", CloudFieldKind.Number),
        new("environments", "Environments", CloudFieldKind.Texts, Shown: false),
    ];

    public static readonly CloudField[] CreatedProject =
    [
        new("organizationName", "Organization Name"),
        new("organizationId", "Organization Id"),
        new("id", "Project Id"),
        new("name", "Project name"),
        new("region", "Region"),
        new("createdBy", "Created by"),
        new("createdAt", "Created at", CloudFieldKind.Date),
    ];

    public static readonly CloudField[] Environment =
    [
        new("organizationId", "Organization Id"),
        new("organizationName", "Organization Name"),
        new("projectId", "Project Id"),
        new("projectName", "Project name"),
        new("id", "Environment Id"),
        new("name", "Environment Name"),
        new("host", "Environment Host"),
        new("tenantType", "Environment Type"),
        new("createdBy", "Created by"),
        new("createdAt", "Created at", CloudFieldKind.Date),
        new("lastUpdatedBy", "Last updated by"),
        new("lastUpdatedAt", "Last updated at", CloudFieldKind.Date),
        new("provisioningStatus", "Provisioning status", CloudFieldKind.StatusName),
        new("provisioningLastFailureMessage", "Provisioning last failure message"),
        new("lastSuccessfulDeploymentId", "Last successful deployment Id"),
        new("type", "Type"),
    ];

    public static readonly CloudField[] CreatedEnvironment =
    [
        new("organizationId", "Organization Id"),
        new("organizationName", "Organization Name"),
        new("projectId", "Project Id"),
        new("projectName", "Project Name"),
        new("id", "Environment Id"),
        new("name", "Environment Name"),
        new("createdBy", "Created by"),
        new("createdAt", "Created at", CloudFieldKind.Date),
    ];

    public static readonly CloudField[] Deployment =
    [
        new("organizationId", "Organization Id"),
        new("projectId", "Project Id"),
        new("environmentId", "Environment Id"),
        new("id", "Deployment Id"),
        new("startedAt", "Started at", CloudFieldKind.OptionalDate),
        new("completedAt", "Completed at", CloudFieldKind.OptionalDate),
        new("provisioningStatus", "Provisioning status", CloudFieldKind.Status),
        new("provisioningStartedAt", "Provisioning started at", CloudFieldKind.OptionalDate),
        new("provisioningCompletedAt", "Provisioning completed at", CloudFieldKind.OptionalDate),
        new("provisioningLastFailureMessage", "Provisioning last failure message"),
        new("buildStatus", "Build status", CloudFieldKind.Status),
        new("buildStartedAt", "Building started at", CloudFieldKind.OptionalDate),
        new("buildCompletedAt", "Building completed at", CloudFieldKind.OptionalDate),
        new("buildLastFailureMessage", "Build last failure message"),
        new("deploymentStatus", "Deployment status", CloudFieldKind.Status),
        new("deploymentStartedAt", "Deployment started at", CloudFieldKind.OptionalDate),
        new("deploymentCompletedAt", "Deployment completed at", CloudFieldKind.OptionalDate),
        new("deploymentLastFailureMessage", "Deployment last failure message"),
        new("postActionStatus", "Post Action status", CloudFieldKind.OptionalStatus),
        new("postActionStartedAt", "Post Action started at", CloudFieldKind.OptionalDate),
        new("postActionCompletedAt", "Post Action completed at", CloudFieldKind.OptionalDate),
        new("postActionLastFailureMessage", "Post Action last failure message"),
        new("calculatedStatus", "Calculated status", CloudFieldKind.Status),
        new("createdBy", "Created by"),
        new("createdAt", "Created at", CloudFieldKind.Date),
        new("triggerMessage", "Trigger message"),
    ];

    public static readonly CloudField[] Variable =
    [
        new("name", "Name"),
        new("secret", "Secret", CloudFieldKind.Flag),
        new("value", "Value"),
        new("target", "Target"),
    ];

    public static readonly CloudField[] Organization =
    [
        new("id", "Id", Json: "Id"),
        new("name", "Name", Json: "Name"),
        new("region", "Region", Json: "Region"),
        new("createdAt", "CreatedAt", CloudFieldKind.Date, Json: "CreatedAt"),
        new("createdBy", "CreatedBy", Json: "CreatedBy"),
        new("tier", "Tier", Json: "Tier"),
    ];

    public static readonly CloudField[] LogFile =
    [
        new("name", "Name", Json: "Name"),
        new("path", "Path", Json: "Path"),
    ];

    public static readonly CloudField[] RestartStatus =
    [
        new("id", "Id", Json: "Id"),
        new("environmentId", "EnvironmentId", Json: "EnvironmentId"),
        new("organizationId", "OrganizationId", Json: "OrganizationId"),
        new("restartStatus", "RestartStatus", CloudFieldKind.Status, Json: "RestartStatus"),
        new("createdAt", "CreatedAt", CloudFieldKind.Date, Json: "CreatedAt"),
        new("createdBy", "CreatedBy", Json: "CreatedBy"),
    ];

    public static readonly CloudField[] DeploymentLogLine =
    [
        new("logTime", "LogTime", CloudFieldKind.OptionalDate, Json: "LogTime"),
        new("logLevel", "LogLevel", Json: "LogLevel"),
        new("logMessage", "LogMessage", Json: "LogMessage"),
        new("stage", "Stage", Json: "Stage"),
        new("organizationId", "OrganizationId", Json: "OrganizationId"),
    ];

    private static readonly Dictionary<string, string> Regions = new(StringComparer.Ordinal)
    {
        ["weu"] = "West Europe",
        ["eus"] = "East US",
        ["wus"] = "West US 2",
        ["aue"] = "Australia East",
        ["jpe"] = "Japan East",
    };

    /// <summary>A region code with its name, e.g. <c>aue (Australia East)</c>, as the Official CLI shows it.</summary>
    public static string DescribeRegion(string? code) =>
        code is null ? string.Empty : Regions.TryGetValue(code, out var name) ? $"{code} ({name})" : code;

    public static IReadOnlyDictionary<string, string> RegionOverride(JsonElement project) =>
        new Dictionary<string, string> { ["region"] = DescribeRegion(CloudJson.String(project, "region")) };

    public static void WriteObject(Utf8JsonWriter w, JsonElement source, IReadOnlyList<CloudField> shape, IReadOnlyDictionary<string, string>? overrides = null)
    {
        w.WriteStartObject();
        foreach (var field in shape)
        {
            w.WritePropertyName(field.JsonName);
            if (overrides is not null && overrides.TryGetValue(field.Api, out var text))
            {
                w.WriteStringValue(text);
                continue;
            }
            var present = CloudJson.TryGet(source, field.Api, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            switch (field.Kind)
            {
                case CloudFieldKind.Date when !present:
                    w.WriteStringValue("0001-01-01T00:00:00");
                    break;
                case CloudFieldKind.Number when !present:
                    w.WriteNumberValue(0);
                    break;
                case CloudFieldKind.Flag when !present:
                    w.WriteBooleanValue(false);
                    break;
                case CloudFieldKind.Texts when !present:
                    w.WriteStartArray();
                    w.WriteEndArray();
                    break;
                case CloudFieldKind.Status:
                    w.WriteNumberValue(present ? StatusNumber(value) : 0);
                    break;
                case CloudFieldKind.OptionalStatus when present:
                    w.WriteNumberValue(StatusNumber(value));
                    break;
                case CloudFieldKind.StatusName:
                    w.WriteStringValue(TaskStatuses[present ? StatusNumber(value) : 0]);
                    break;
                default:
                    if (present)
                    {
                        value.WriteTo(w);
                    }
                    else
                    {
                        w.WriteNullValue();
                    }
                    break;
            }
        }
        w.WriteEndObject();
    }

    public static void WriteArray(Utf8JsonWriter w, IEnumerable<JsonElement> items, IReadOnlyList<CloudField> shape, Func<JsonElement, IReadOnlyDictionary<string, string>?>? overrides = null)
    {
        w.WriteStartArray();
        foreach (var item in items)
        {
            WriteObject(w, item, shape, overrides?.Invoke(item));
        }
        w.WriteEndArray();
    }

    /// <summary>The <c>Label : value</c> rows the Official CLI prints for an object, labels padded to one width.</summary>
    public static IEnumerable<string> Table(JsonElement source, IReadOnlyList<CloudField> shape, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var rows = shape.Where(f => f.Shown).Select(f => (f.Label, Value: overrides is not null && overrides.TryGetValue(f.Api, out var o) ? o : Display(source, f))).ToList();
        var width = rows.Max(r => r.Label.Length) + 1;
        return rows.Select(r => $"{r.Label.PadRight(width)}: {r.Value}");
    }

    public static string Display(JsonElement source, CloudField field)
    {
        var present = CloudJson.TryGet(source, field.Api, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        return field.Kind switch
        {
            CloudFieldKind.Date => present ? FormatDate(value) : DateTime.MinValue.ToString(CultureInfo.CurrentCulture),
            CloudFieldKind.OptionalDate => present ? FormatDate(value) : string.Empty,
            CloudFieldKind.Number => present ? value.ToString() : "0",
            CloudFieldKind.Flag => present && value.ValueKind == JsonValueKind.True ? "True" : "False",
            CloudFieldKind.Status or CloudFieldKind.StatusName => TaskStatuses[present ? StatusNumber(value) : 0],
            CloudFieldKind.OptionalStatus => present ? TaskStatuses[StatusNumber(value)] : string.Empty,
            CloudFieldKind.Texts => present ? string.Join(", ", CloudJson.Items(value).Select(v => v.ToString())) : string.Empty,
            _ => present ? (value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString()) : string.Empty,
        };
    }

    /// <summary>A status number from either form the API may use: a number or a name.</summary>
    public static int StatusNumber(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= 0 && n < TaskStatuses.Length)
        {
            return n;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            var index = Array.FindIndex(TaskStatuses, t => t.Equals(s, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                return index;
            }
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 && n < TaskStatuses.Length)
            {
                return n;
            }
        }
        return 0;
    }

    public static int Status(JsonElement source, string field) => CloudJson.TryGet(source, field, out var v) && v.ValueKind != JsonValueKind.Null ? StatusNumber(v) : 0;

    public static int? OptionalStatus(JsonElement source, string field) => CloudJson.TryGet(source, field, out var v) && v.ValueKind != JsonValueKind.Null ? StatusNumber(v) : null;

    /// <summary>A timestamp shown the way .NET prints a UTC date in the current culture, as the Official CLI shows it.</summary>
    public static string FormatDate(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && TryParseDate(value.GetString(), out var date)
            ? date.ToString(CultureInfo.CurrentCulture)
            : value.ToString();

    public static bool TryParseDate(string? text, out DateTime utc)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            utc = parsed.UtcDateTime;
            return true;
        }
        utc = default;
        return false;
    }

    public static DateTime? Date(JsonElement source, string field) =>
        CloudJson.String(source, field) is { } s && TryParseDate(s, out var d) ? d : null;
}
