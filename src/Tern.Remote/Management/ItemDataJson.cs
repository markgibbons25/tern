using System.Text.Json;
using Tern.Core.Items;

namespace Tern.Remote.Management;

/// <summary>Converts between <see cref="ItemData"/> and the JSON item shape Management Services uses.</summary>
public static class ItemDataJson
{
    public static ItemData Parse(JsonElement e)
    {
        var item = new ItemData
        {
            Id = Guid.Parse(e.GetProperty("id").GetString()!),
            ParentId = Guid.Parse(e.GetProperty("parentId").GetString()!),
            TemplateId = Guid.Parse(e.GetProperty("templateId").GetString()!),
            Path = e.GetProperty("path").GetString()!.TrimEnd('/'),
            BranchId = e.TryGetProperty("branchId", out var b) && b.ValueKind == JsonValueKind.String ? Guid.Parse(b.GetString()!) : Guid.Empty,
        };
        if (e.TryGetProperty("sharedFields", out var shared) && shared.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in shared.EnumerateArray())
            {
                item.SharedFields.Add(ParseField(f));
            }
        }
        if (e.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in versions.EnumerateArray())
            {
                var language = item.Languages.GetOrAdd(v.GetProperty("language").GetString()!);
                var version = language.Versions.GetOrAdd(v.GetProperty("versionNumber").GetInt32());
                if (v.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fields.EnumerateArray())
                    {
                        version.Fields.Add(ParseField(f));
                    }
                }
            }
        }
        if (e.TryGetProperty("unversionedFields", out var unversioned) && unversioned.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in unversioned.EnumerateArray())
            {
                var language = item.Languages.GetOrAdd(l.GetProperty("language").GetString()!);
                if (l.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fields.EnumerateArray())
                    {
                        language.UnversionedFields.Add(ParseField(f));
                    }
                }
            }
        }
        return item;
    }

    private static FieldValue ParseField(JsonElement f)
    {
        Guid? blobId = f.TryGetProperty("blobId", out var b) && b.ValueKind == JsonValueKind.String && Guid.TryParse(b.GetString(), out var g) ? g : null;
        return new FieldValue(
            Guid.Parse(f.GetProperty("fieldId").GetString()!),
            f.TryGetProperty("nameHint", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null,
            blobId,
            f.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty);
    }

    /// <summary>Writes the item in the shape a CREATE command carries.</summary>
    public static void Write(Utf8JsonWriter w, ItemData item)
    {
        w.WriteStartObject();
        w.WriteString("id", item.Id);
        w.WriteString("parentId", item.ParentId);
        w.WriteString("templateId", item.TemplateId);
        w.WriteString("path", item.Path);
        w.WriteString("name", item.Name);
        if (item.BranchId != Guid.Empty)
        {
            w.WriteString("branchId", item.BranchId);
        }
        w.WriteStartArray("sharedFields");
        foreach (var f in item.SharedFields)
        {
            WriteField(w, f);
        }
        w.WriteEndArray();
        w.WriteStartArray("unversionedFields");
        foreach (var l in item.Languages.Where(l => l.UnversionedFields.Count > 0))
        {
            w.WriteStartObject();
            w.WriteString("language", l.Name);
            w.WriteStartArray("fields");
            foreach (var f in l.UnversionedFields)
            {
                WriteField(w, f);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("versions");
        foreach (var (l, v) in item.AllVersions)
        {
            w.WriteStartObject();
            w.WriteString("language", l.Name);
            w.WriteNumber("versionNumber", v.Number);
            w.WriteStartArray("fields");
            foreach (var f in v.Fields)
            {
                WriteField(w, f);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static void WriteField(Utf8JsonWriter w, FieldValue f)
    {
        w.WriteStartObject();
        w.WriteString("fieldId", f.Id);
        if (f.Hint is not null)
        {
            w.WriteString("nameHint", f.Hint);
        }
        w.WriteString("value", f.Value);
        if (f.BlobId is { } blobId)
        {
            w.WriteString("blobId", blobId);
        }
        w.WriteEndObject();
    }
}
