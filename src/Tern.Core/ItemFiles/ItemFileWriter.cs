using System.Globalization;
using System.Text;
using Tern.Core.Items;

namespace Tern.Core.ItemFiles;

/// <summary>
/// Writes Item files byte for byte as the Official CLI does: UTF-8 with a BOM, two-space
/// indentation, and the platform's line ending (CRLF on Windows, LF elsewhere).
/// </summary>
public static class ItemFileWriter
{
    private static readonly char[] BlockTriggers = ['\n', '\r', '"', '\\'];
    private static readonly char[] QuoteTriggers = ['"', ':', '[', ']', '{', '}', '!', '?', '-'];
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public static byte[] ToBytes(ItemData item, string? newLine = null)
    {
        var text = ToText(item, newLine ?? Environment.NewLine);
        var bytes = new byte[Bom.Length + Encoding.UTF8.GetByteCount(text)];
        Bom.CopyTo(bytes, 0);
        Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, Bom.Length);
        return bytes;
    }

    /// <summary>
    /// Writes the item's file to <paramref name="stream"/>, the same bytes as <see cref="ToBytes"/>,
    /// encoding as it goes rather than building the whole text and then a copy of it in bytes:
    /// a media item's blob can be many megabytes.
    /// </summary>
    public static void WriteTo(Stream stream, ItemData item, string? newLine = null)
    {
        stream.Write(Bom);
        Build(item, newLine ?? Environment.NewLine).CopyTo(stream);
    }

    public static string ToText(ItemData item, string newLine) => Build(item, newLine).ToString();

    private static Writer Build(ItemData item, string newLine)
    {
        var w = new Writer(newLine);
        w.Line("---");
        w.Map("ID", Format(item.Id));
        w.Map("Parent", Format(item.ParentId));
        w.Map("Template", Format(item.TemplateId));
        w.Map("Path", item.Path);
        if (item.BranchId != Guid.Empty)
        {
            w.Map("BranchID", Format(item.BranchId));
        }
        if (item.SharedFields.Count > 0)
        {
            w.Key("SharedFields");
            w.Indent += 2;
            foreach (var field in item.SharedFields)
            {
                WriteField(w, field);
            }
            w.Indent -= 2;
        }
        var languages = item.Languages.Where(l => l.UnversionedFields.Count > 0 || l.Versions.Count > 0).ToList();
        if (languages.Count > 0)
        {
            w.Key("Languages");
            w.Indent += 2;
            foreach (var language in languages)
            {
                if (string.IsNullOrWhiteSpace(language.Name))
                {
                    throw new InvalidOperationException($"Item {item.Path} has a version with a blank language.");
                }
                w.ListItem("Language", language.Name);
                if (language.UnversionedFields.Count > 0)
                {
                    w.Key("Fields");
                    w.Indent += 2;
                    foreach (var field in language.UnversionedFields)
                    {
                        WriteField(w, field);
                    }
                    w.Indent -= 2;
                }
                w.Key("Versions");
                w.Indent += 2;
                foreach (var version in language.Versions)
                {
                    w.ListItem("Version", version.Number.ToString(CultureInfo.InvariantCulture));
                    if (version.Fields.Count > 0)
                    {
                        w.Key("Fields");
                        w.Indent += 2;
                        foreach (var field in version.Fields)
                        {
                            WriteField(w, field);
                        }
                        w.Indent -= 2;
                    }
                }
                w.Indent -= 2;
            }
            w.Indent -= 2;
        }
        if (!string.IsNullOrEmpty(item.Database))
        {
            w.Map("DB", item.Database);
        }
        return w;
    }

    private static void WriteField(Writer w, FieldValue field)
    {
        w.ListItem("ID", Format(field.Id));
        if (!string.IsNullOrWhiteSpace(field.Hint))
        {
            w.Map("Hint", field.Hint);
        }
        if (field.BlobId is { } blobId)
        {
            w.Map("BlobID", Format(blobId));
        }
        w.Map("Value", field.Value);
    }

    public static string Format(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>A minimal line writer that mirrors the Official CLI's map, list and block rules.</summary>
    public sealed class Writer(string newLine)
    {
        private readonly StringBuilder _sb = new();

        public int Indent { get; set; }

        public void Line(string text) => _sb.Append(text).Append(newLine);

        public void Key(string key) => _sb.Append(' ', Indent).Append(key).Append(':').Append(newLine);

        public void Map(string key, string value)
        {
            _sb.Append(' ', Indent);
            MapBody(key, value);
        }

        public void ListItem(string key, string value)
        {
            if (Indent < 2)
            {
                throw new InvalidOperationException("A list needs an indent of at least two.");
            }
            _sb.Append(' ', Indent - 2).Append("- ");
            MapBody(key, value);
        }

        private void MapBody(string key, string value)
        {
            if (value.IndexOfAny(BlockTriggers) >= 0)
            {
                _sb.Append(key).Append(": |").Append(newLine);
                AppendBlock(Indent + 2, value);
                _sb.Append(newLine);
            }
            else
            {
                _sb.Append(key).Append(": ");
                if (value.IndexOfAny(QuoteTriggers) < 0)
                {
                    _sb.Append(value);
                }
                else
                {
                    _sb.Append('"').Append(value.Replace("\"", "\\\"")).Append('"');
                }
                _sb.Append(newLine);
            }
        }

        // Every line break becomes the platform newline followed by the block indent. A CR that
        // starts a CRLF pair, or that ends the value, produces nothing.
        private void AppendBlock(int indent, string value)
        {
            _sb.Append(' ', indent);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '\r':
                        if (i < value.Length - 1 && value[i + 1] != '\n')
                        {
                            _sb.Append(newLine).Append(' ', indent);
                        }
                        break;
                    case '\n':
                        _sb.Append(newLine).Append(' ', indent);
                        break;
                    default:
                        _sb.Append(c);
                        break;
                }
            }
        }

        public override string ToString() => _sb.ToString();

        /// <summary>Encodes the text as UTF-8 into <paramref name="stream"/>, one chunk of the builder at a time.</summary>
        public void CopyTo(Stream stream)
        {
            var encoder = new UTF8Encoding(false).GetEncoder();
            var buffer = new byte[64 * 1024];
            foreach (var chunk in _sb.GetChunks())
            {
                var chars = chunk.Span;
                while (!chars.IsEmpty)
                {
                    encoder.Convert(chars, buffer, flush: false, out var used, out var bytes, out _);
                    stream.Write(buffer, 0, bytes);
                    chars = chars[used..];
                }
            }
            encoder.Convert(ReadOnlySpan<char>.Empty, buffer, flush: true, out _, out var tail, out _);
            stream.Write(buffer, 0, tail);
        }
    }
}
