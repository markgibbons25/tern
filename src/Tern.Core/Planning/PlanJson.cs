using System.Globalization;
using System.Text.Json;

namespace Tern.Core.Planning;

/// <summary>The saved form of a Plan: a public, versioned contract (ADR 0008).</summary>
public static class PlanJson
{
    public static void Write(Utf8JsonWriter w, Plan plan)
    {
        w.WriteStartObject();
        w.WriteNumber("planVersion", Plan.FormatVersion);
        w.WriteString("direction", plan.Direction.ToString().ToLowerInvariant());
        w.WriteString("source", plan.Source);
        w.WriteString("target", plan.Target);
        w.WriteString("createdAt", plan.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        w.WriteStartObject("summary");
        foreach (var action in Enum.GetValues<PlanAction>())
        {
            w.WriteNumber(action.ToString().ToLowerInvariant(), plan.Count(action));
        }
        w.WriteEndObject();
        w.WriteStartArray("warnings");
        foreach (var warning in plan.Warnings)
        {
            w.WriteStringValue(warning);
        }
        w.WriteEndArray();
        if (plan.ExcludedFields is { } excluded)
        {
            w.WriteStartArray("excludedFields");
            foreach (var id in excluded)
            {
                w.WriteStringValue(id);
            }
            w.WriteEndArray();
        }
        if (plan.AdHocIncludes.Count > 0)
        {
            w.WriteStartArray("adHocIncludes");
            foreach (var include in plan.AdHocIncludes)
            {
                w.WriteStartObject();
                w.WriteString("module", include.Module);
                w.WriteString("name", include.Name);
                w.WriteString("path", include.Path);
                w.WriteString("database", include.Database);
                w.WriteString("scope", include.Scope);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteStartArray("operations");
        foreach (var op in plan.Operations)
        {
            WriteOperation(w, op);
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static void WriteOperation(Utf8JsonWriter w, PlanOperation op)
    {
        w.WriteStartObject();
        w.WriteString("action", op.Action.ToString().ToLowerInvariant());
        w.WriteString("module", op.Module);
        w.WriteString("include", op.Include);
        w.WriteString("database", op.Database);
        w.WriteString("id", op.Id);
        w.WriteString("path", op.Path);
        if (op.TargetPath is not null)
        {
            w.WriteString("targetPath", op.TargetPath);
        }
        w.WriteString("parentId", op.ParentId);
        if (op.SourceSignature is not null)
        {
            w.WriteString("sourceSignature", op.SourceSignature);
        }
        if (op.ExpectedSignature is not null)
        {
            w.WriteString("expectedSignature", op.ExpectedSignature);
        }
        w.WriteEndObject();
    }

    public static byte[] ToBytes(Plan plan)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            Write(w, plan);
        }
        return stream.ToArray();
    }

    public static Plan Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.GetProperty("planVersion").GetInt32();
        if (version != Plan.FormatVersion)
        {
            throw new InvalidDataException($"This Plan has format version {version}; this Tern reads version {Plan.FormatVersion}.");
        }
        var plan = new Plan
        {
            Direction = Enum.Parse<PlanDirection>(root.GetProperty("direction").GetString()!, ignoreCase: true),
            Source = root.GetProperty("source").GetString()!,
            Target = root.GetProperty("target").GetString()!,
            CreatedAt = DateTimeOffset.Parse(root.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture),
        };
        foreach (var warning in root.GetProperty("warnings").EnumerateArray())
        {
            plan.Warnings.Add(warning.GetString()!);
        }
        if (root.TryGetProperty("excludedFields", out var excluded))
        {
            plan.ExcludedFields = [.. excluded.EnumerateArray().Select(e => e.GetGuid())];
        }
        if (root.TryGetProperty("adHocIncludes", out var adHoc))
        {
            foreach (var i in adHoc.EnumerateArray())
            {
                plan.AdHocIncludes.Add(new PlanInclude
                {
                    Module = i.GetProperty("module").GetString()!,
                    Name = i.GetProperty("name").GetString()!,
                    Path = i.GetProperty("path").GetString()!,
                    Database = i.GetProperty("database").GetString()!,
                    Scope = i.GetProperty("scope").GetString()!,
                });
            }
        }
        foreach (var o in root.GetProperty("operations").EnumerateArray())
        {
            plan.Operations.Add(new PlanOperation
            {
                Action = Enum.Parse<PlanAction>(o.GetProperty("action").GetString()!, ignoreCase: true),
                Module = o.GetProperty("module").GetString()!,
                Include = o.GetProperty("include").GetString()!,
                Database = o.GetProperty("database").GetString()!,
                Id = o.GetProperty("id").GetGuid(),
                Path = o.GetProperty("path").GetString()!,
                TargetPath = o.TryGetProperty("targetPath", out var tp) ? tp.GetString() : null,
                ParentId = o.GetProperty("parentId").GetGuid(),
                SourceSignature = o.TryGetProperty("sourceSignature", out var ss) ? ss.GetString() : null,
                ExpectedSignature = o.TryGetProperty("expectedSignature", out var es) ? es.GetString() : null,
            });
        }
        return plan;
    }
}
