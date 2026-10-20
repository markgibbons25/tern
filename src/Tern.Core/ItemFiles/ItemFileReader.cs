using System.Globalization;
using System.Text;
using Tern.Core.Items;

namespace Tern.Core.ItemFiles;

public sealed class ItemFileFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads Item files with the Official CLI's line-based rules. This is not a general YAML parser:
/// keys come in a fixed order, a map key runs to the first colon, and block values are joined
/// with CRLF on every platform.
/// </summary>
public static class ItemFileReader
{
    public static ItemData Read(ReadOnlySpan<byte> bytes, string? sourcePath = null, Func<Guid, bool>? includeField = null, bool lenient = true)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bytes = bytes[3..];
        }
        return Read(Encoding.UTF8.GetString(bytes), sourcePath, includeField, lenient);
    }

    public static ItemData Read(string text, string? sourcePath = null, Func<Guid, bool>? includeField = null, bool lenient = true)
    {
        var parser = new LineParser(text, lenient);
        try
        {
            if (!parser.AdvanceToDocument())
            {
                throw new ItemFileFormatException("The file does not start a YAML document with '---'.");
            }
            return ReadItem(parser, includeField ?? (_ => true));
        }
        catch (Exception ex) when (ex is not ItemFileFormatException || sourcePath is not null)
        {
            throw new ItemFileFormatException($"Error parsing YAML {sourcePath}: {ex.Message}", ex);
        }
    }

    private static ItemData ReadItem(LineParser p, Func<Guid, bool> includeField)
    {
        var id = p.ExpectedGuid("ID");
        var parent = p.ExpectedGuid("Parent");
        var template = p.ExpectedGuid("Template");
        var path = p.Expected("Path");
        if (!path.StartsWith('/'))
        {
            throw new ItemFileFormatException(p.Error($"Item path {path} did not start with /."));
        }
        var item = new ItemData { Id = id, ParentId = parent, TemplateId = template, Path = path.TrimEnd('/') };

        // The Official CLI reads DB only straight after Path, although it writes it at the end.
        if (p.Optional("DB", out var db))
        {
            item.Database = db;
        }
        p.Optional("Created", out _);
        if (p.Optional("BranchID", out var branch))
        {
            item.BranchId = ParseGuid(p, branch);
        }
        if (p.Optional("SharedFields", out _))
        {
            while (ReadField(p) is { } field)
            {
                if (includeField(field.Id))
                {
                    item.SharedFields.Add(field);
                }
            }
        }
        if (!p.Optional("Languages", out _))
        {
            return item;
        }
        while (p.Optional("Language", out var languageName))
        {
            if (string.IsNullOrWhiteSpace(languageName))
            {
                throw new ItemFileFormatException(p.Error("Read YAML version with empty language value."));
            }
            var language = new ItemLanguage(languageName);
            if (p.Optional("Fields", out _))
            {
                while (ReadField(p) is { } field)
                {
                    if (includeField(field.Id))
                    {
                        language.UnversionedFields.Add(field);
                    }
                }
            }
            p.Expected("Versions");
            while (p.Optional("Version", out var versionNumber))
            {
                var version = new ItemVersion(int.Parse(versionNumber, CultureInfo.InvariantCulture));
                if (p.Optional("Fields", out _))
                {
                    while (ReadField(p) is { } field)
                    {
                        if (includeField(field.Id))
                        {
                            version.Fields.Add(field);
                        }
                    }
                }
                language.Versions.Add(version);
            }
            item.Languages.Add(language);
        }
        return item;
    }

    private static FieldValue? ReadField(LineParser p)
    {
        var next = p.Peek();
        if (next is null)
        {
            return null;
        }
        if (next.Value.Key != "ID")
        {
            if (next.Value.Key is "Languages" or "Language" or "Version" or "Versions")
            {
                return null;
            }
            throw new ItemFileFormatException(p.Error($"Cannot read field value. Expected 'ID' map, found '{next.Value.Key}' instead."));
        }
        var id = p.ExpectedGuid("ID");
        p.Optional("Hint", out var hint);
        Guid? blobId = p.Optional("BlobID", out var blob) ? ParseGuid(p, blob) : null;
        p.Optional("Type", out _);
        var value = p.Expected("Value");
        return new FieldValue(id, hint, blobId, value);
    }

    private static Guid ParseGuid(LineParser p, string text) =>
        Guid.TryParseExact(text, "D", out var guid)
            ? guid
            : throw new ItemFileFormatException(p.Error($"YAML map value was not the valid GUID that was expected. Got {text}"));

    internal readonly record struct Map(string Key, string Value);

    /// <summary>Splits text into lines exactly as <see cref="StreamReader.ReadLine"/> does.</summary>
    internal static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n')
            {
                lines.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }
        return lines;
    }

    internal static int GetIndent(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != ' ' && line[i] != '-')
            {
                return i;
            }
        }
        return line.Length;
    }

    internal sealed class LineParser(string text, bool lenient)
    {
        private readonly List<string> _lines = SplitLines(text);
        private int _next;
        private Map? _peeked;

        public string Error(string message) => $"Line {_next}: {message}";

        public bool AdvanceToDocument()
        {
            while (_next < _lines.Count)
            {
                if (_lines[_next++] == "---")
                {
                    return _next < _lines.Count;
                }
            }
            return false;
        }

        public Map? Peek() => _peeked ??= ReadMap();

        public bool Optional(string key, out string value)
        {
            var map = Peek();
            if (map is { } m && m.Key == key)
            {
                _peeked = null;
                value = m.Value;
                return true;
            }
            value = null!;
            return false;
        }

        public string Expected(string key)
        {
            var map = Peek() ?? throw new ItemFileFormatException(Error($"Unable to read expected YAML map '{key}'; found end of file instead."));
            if (map.Key != key)
            {
                throw new ItemFileFormatException(Error($"Expected YAML map key '{key}' was not found. Instead found '{map.Key}'"));
            }
            _peeked = null;
            return map.Value;
        }

        public Guid ExpectedGuid(string key) => ParseGuid(this, Expected(key));

        private Map? ReadMap()
        {
            var line = NextDataLine();
            if (line is null)
            {
                return null;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new ItemFileFormatException(Error($"Invalid YAML map value (no : found) {line}"));
            }
            var indent = GetIndent(line);
            if (indent > colon)
            {
                throw new ItemFileFormatException(Error($"Invalid YAML map key {line}"));
            }
            var key = line[indent..colon];
            if (line.Length < colon + 2)
            {
                return new Map(key, string.Empty);
            }
            var rest = line[(colon + 2)..];
            return new Map(key, rest.StartsWith('|') ? ReadBlock(indent + 2) : Decode(rest));
        }

        private string? NextDataLine()
        {
            while (_next < _lines.Count)
            {
                var line = _lines[_next++];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                var indent = GetIndent(line);
                if (line.Length <= indent || line[indent] != '#')
                {
                    return line;
                }
            }
            return null;
        }

        private string ReadBlock(int indent)
        {
            var sb = new StringBuilder();
            var first = true;
            while (_next < _lines.Count)
            {
                var line = _lines[_next];
                if (GetIndent(line) < indent)
                {
                    // Deviation D1: an editor that trims trailing whitespace turns an empty block
                    // line into a blank one. The Official CLI ends the block there and then fails
                    // on the next line; when the block plainly continues, we keep reading.
                    if (!lenient || !string.IsNullOrWhiteSpace(line) || !BlockContinuesAfterBlankLines(indent))
                    {
                        break;
                    }
                    line = new string(' ', indent);
                }
                if (!first)
                {
                    sb.Append("\r\n");
                }
                first = false;
                sb.Append(line, indent, line.Length - indent);
                _next++;
            }
            return sb.ToString();
        }

        private bool BlockContinuesAfterBlankLines(int indent)
        {
            for (var i = _next; i < _lines.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(_lines[i]))
                {
                    return GetIndent(_lines[i]) >= indent;
                }
            }
            return false;
        }

        private static string Decode(string value)
        {
            if (value.Length > 2 && value[0] == '"' && value[^1] == '"')
            {
                return value[1..^1].Replace("\\\"", "\"");
            }
            return value;
        }
    }
}
