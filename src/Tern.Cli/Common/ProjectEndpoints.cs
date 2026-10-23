using System.Text.Json;

using Tern.Remote.Login;

namespace Tern.Cli.Common;

/// <summary>
/// The <c>endpoints</c> of a project's committed <c>tern.json</c> (ADR 0013), so a team shares where each
/// environment is and whether it may be written to. Secrets stay in <c>user.json</c>, <c>.env</c> or the
/// environment, so a property that could hold one is refused rather than ignored.
/// </summary>
public static class ProjectEndpoints
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "host", "authority", "audience", "allowWrite", "useClientCredentials" };

    /// <param name="file">The project's <c>tern.json</c> (see <see cref="TernFolder.ConfigFile"/>), or null when it has none.</param>
    public static IReadOnlyList<SharedEndpoint> Load(string? file)
    {
        if (file is null || !File.Exists(file))
        {
            return [];
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("endpoints", out var endpoints))
        {
            return [];
        }
        if (endpoints.ValueKind != JsonValueKind.Object)
        {
            throw new CliException($"{file}: \"endpoints\" must be an object of endpoint names.");
        }
        var result = new List<SharedEndpoint>();
        foreach (var e in endpoints.EnumerateObject())
        {
            if (e.Value.ValueKind != JsonValueKind.Object)
            {
                throw new CliException($"{file}: endpoint '{e.Name}' must be an object.");
            }
            foreach (var p in e.Value.EnumerateObject().Where(p => !Allowed.Contains(p.Name)))
            {
                throw new CliException($"{file}: endpoint '{e.Name}' has \"{p.Name}\", which tern.json can't hold. It takes only {string.Join(", ", Allowed)}; keep client credentials in .env or TERN_* variables.");
            }
            var host = Str(e.Value, "host") ?? throw new CliException($"{file}: endpoint '{e.Name}' needs a \"host\".");
            result.Add(new SharedEndpoint(e.Name, host, Str(e.Value, "authority"), Str(e.Value, "audience"), Bool(e.Value, "allowWrite"), Bool(e.Value, "useClientCredentials")));
        }
        return result;

        string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : throw Type(name, "a string") : null;
        bool? Bool(JsonElement o, string name) => o.TryGetProperty(name, out var v) ? v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw Type(name, "true or false") : null;
        CliException Type(string name, string expected) => new($"{file}: \"{name}\" must be {expected}.");
    }
}
