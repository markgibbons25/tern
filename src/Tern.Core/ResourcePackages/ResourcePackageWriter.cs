using System.Text;
using System.Xml;
using System.Xml.Linq;

using Tern.Core.Items;

namespace Tern.Core.ResourcePackages;

/// <summary>
/// Writes an items-as-resources package (<c>items.&lt;db&gt;.&lt;name&gt;.dat</c>): the protobuf file
/// a SitecoreAI CM loads as read-only items. The layout is protobuf-net's encoding of the
/// package contract: maps are repeated key/value pairs, a nested map's entries repeat inline as
/// field 2, and a Guid is a two-field message of its byte halves.
/// </summary>
public static class ResourcePackageWriter
{
    private static readonly string[] XmlHints = ["__Renderings", "__Final Renderings"];

    /// <summary>
    /// Writes <paramref name="items"/> in the order given. Versions without fields are left out,
    /// and <paramref name="warn"/> hears about each one.
    /// </summary>
    public static byte[] Write(IReadOnlyList<ItemData> items, Action<string>? warn = null)
    {
        var root = new ProtoBuffer();
        foreach (var item in items)
        {
            var record = new ProtoBuffer();
            record.Guid(1, item.Id);
            record.String(2, item.Name);
            record.Guid(3, item.ParentId);
            record.Guid(4, item.TemplateId);
            record.Guid(5, item.BranchId);
            var entry = new ProtoBuffer();
            entry.Guid(1, item.Id, always: true);
            entry.Message(2, record);
            root.Message(1, entry);
        }
        foreach (var item in items.Where(i => i.SharedFields.Count > 0))
        {
            var entry = new ProtoBuffer();
            entry.Guid(1, item.Id, always: true);
            foreach (var f in item.SharedFields)
            {
                entry.Message(2, Field(f));
            }
            root.Message(2, entry);
        }
        foreach (var item in items)
        {
            // Languages appear in the order fields first reach them: unversioned fields (as
            // version 0) before versioned ones, exactly as the package builder adds them.
            var languages = new List<(string Name, List<(int Version, List<FieldValue> Fields)> Versions)>();
            List<FieldValue> Bucket(string language, int version)
            {
                var l = languages.FirstOrDefault(x => x.Name == language);
                if (l.Name is null)
                {
                    l = (language, []);
                    languages.Add(l);
                }
                var v = l.Versions.FirstOrDefault(x => x.Version == version);
                if (v.Fields is null)
                {
                    v = (version, []);
                    l.Versions.Add(v);
                }
                return v.Fields;
            }
            foreach (var language in item.Languages)
            {
                foreach (var f in language.UnversionedFields)
                {
                    Bucket(language.Name, 0).Add(f);
                }
            }
            foreach (var language in item.Languages)
            {
                foreach (var version in language.Versions)
                {
                    if (version.Fields.Count == 0)
                    {
                        warn?.Invoke($"Item {item.Path} {{{item.Id}}} with language: ({language.Name}) in version: ({version.Number}) doesn't have any fields. This version will be ignored.");
                        continue;
                    }
                    foreach (var f in version.Fields)
                    {
                        Bucket(language.Name, version.Number).Add(f);
                    }
                }
            }
            if (languages.Count == 0)
            {
                continue;
            }
            var entry = new ProtoBuffer();
            entry.Guid(1, item.Id, always: true);
            foreach (var (name, versions) in languages)
            {
                var langEntry = new ProtoBuffer();
                langEntry.String(1, name);
                foreach (var (number, fields) in versions)
                {
                    var versionEntry = new ProtoBuffer();
                    versionEntry.Int(1, number);
                    foreach (var f in fields)
                    {
                        versionEntry.Message(2, Field(f));
                    }
                    langEntry.Message(2, versionEntry);
                }
                entry.Message(2, langEntry);
            }
            root.Message(3, entry);
        }
        return root.ToArray();
    }

    private static ProtoBuffer Field(FieldValue f)
    {
        var b = new ProtoBuffer();
        b.Guid(1, f.Id, always: true);
        b.String(2, Unformat(f.Value, f.Hint));
        return b;
    }

    /// <summary>
    /// Turns a serialized value back into what Sitecore stores, with the Official CLI's heuristics:
    /// two or more line-separated GUIDs become a pipe-separated list, and layout XML is compacted.
    /// </summary>
    public static string Unformat(string value, string? hint)
    {
        var lines = value.Split(Environment.NewLine);
        if (lines.Length > 1 && lines.All(l => Guid.TryParse(l, out _)))
        {
            value = string.Join('|', lines);
        }
        if (!string.IsNullOrWhiteSpace(value) && hint is not null && XmlHints.Contains(hint, StringComparer.InvariantCultureIgnoreCase))
        {
            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false, NewLineOnAttributes = false }))
            {
                XElement.Parse(value).Save(writer);
            }
            value = sb.ToString();
        }
        return value;
    }

    /// <summary>The few protobuf writes the package needs.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A MemoryStream holds only managed memory.")]
    private sealed class ProtoBuffer
    {
        private readonly MemoryStream _stream = new();

        public void Int(int field, int value)
        {
            Tag(field, 0);
            Varint((ulong)(uint)value);
        }

        public void String(int field, string? value)
        {
            if (value is null)
            {
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(value);
            Tag(field, 2);
            Varint((ulong)bytes.Length);
            _stream.Write(bytes);
        }

        /// <summary>A Guid as protobuf-net's bcl.Guid: fixed64 low half, fixed64 high half. An empty Guid is left out.</summary>
        public void Guid(int field, Guid value, bool always = false)
        {
            if (value == System.Guid.Empty && !always)
            {
                return;
            }
            var bytes = value.ToByteArray();
            var inner = new ProtoBuffer();
            if (BitConverter.ToUInt64(bytes, 0) != 0)
            {
                inner.Tag(1, 1);
                inner._stream.Write(bytes, 0, 8);
            }
            if (BitConverter.ToUInt64(bytes, 8) != 0)
            {
                inner.Tag(2, 1);
                inner._stream.Write(bytes, 8, 8);
            }
            Message(field, inner);
        }

        public void Message(int field, ProtoBuffer message)
        {
            Tag(field, 2);
            var bytes = message.ToArray();
            Varint((ulong)bytes.Length);
            _stream.Write(bytes);
        }

        public byte[] ToArray() => _stream.ToArray();

        private void Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

        private void Varint(ulong value)
        {
            while (value >= 0x80)
            {
                _stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            _stream.WriteByte((byte)value);
        }
    }
}
