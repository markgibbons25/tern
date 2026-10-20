using System.Text;
using Tern.Core.ItemFiles;
using Tern.Core.Items;

namespace Tern.Core.Tests.ItemFiles;

public class ItemFileFormatTests
{
    private static ItemData Item(params FieldValue[] shared)
    {
        var item = new ItemData
        {
            Id = new Guid("11111111-2222-3333-4444-555555555555"),
            ParentId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            TemplateId = new Guid("76036f5e-cbce-46d1-af0a-4143f9b557aa"),
            Path = "/sitecore/content/Home",
        };
        foreach (var f in shared)
        {
            item.SharedFields.Add(f);
        }
        return item;
    }

    private static FieldValue Field(string id, string value, string? hint = "Title") => new(new Guid(id), hint, null, value);

    [Fact]
    public void Writes_a_BOM_and_the_header_in_fixed_order()
    {
        var bytes = ItemFileWriter.ToBytes(Item(), "\r\n");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(
            "---\r\nID: \"11111111-2222-3333-4444-555555555555\"\r\nParent: \"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"\r\nTemplate: \"76036f5e-cbce-46d1-af0a-4143f9b557aa\"\r\nPath: /sitecore/content/Home\r\n",
            Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Theory]
    [InlineData("plain text", "Value: plain text")]
    [InlineData("has: colon", "Value: \"has: colon\"")]
    [InlineData("a-b", "Value: \"a-b\"")]
    [InlineData("", "Value: ")]
    [InlineData("  spaced  ", "Value:   spaced  ")]
    public void Encodes_single_line_values_like_the_Official_CLI(string value, string expectedLine)
    {
        var text = ItemFileWriter.ToText(Item(Field("00000000-0000-0000-0000-000000000001", value)), "\n");
        Assert.Contains("\n  " + expectedLine + "\n", text);
    }

    [Fact]
    public void Writes_multi_line_values_as_blocks_and_keeps_a_trailing_empty_line()
    {
        var text = ItemFileWriter.ToText(Item(Field("00000000-0000-0000-0000-000000000001", "one\r\ntwo\n")), "\n");
        Assert.Contains("  Value: |\n    one\n    two\n    \n", text);
    }

    [Fact]
    public void A_quote_or_backslash_forces_a_block()
    {
        var text = ItemFileWriter.ToText(Item(Field("00000000-0000-0000-0000-000000000001", "say \"hi\"")), "\n");
        Assert.Contains("  Value: |\n    say \"hi\"\n", text);
    }

    [Fact]
    public void Drops_a_lone_carriage_return_at_the_end_of_a_block()
    {
        var text = ItemFileWriter.ToText(Item(Field("00000000-0000-0000-0000-000000000001", "x\\y\r")), "\n");
        Assert.Contains("  Value: |\n    x\\y\n", text);
    }

    [Fact]
    public void Sorts_fields_by_guid_and_keeps_the_first_duplicate()
    {
        var item = Item(
            Field("ffffffff-0000-0000-0000-000000000000", "last"),
            Field("00000000-0000-0000-0000-000000000001", "first"),
            Field("00000000-0000-0000-0000-000000000001", "duplicate"));
        var text = ItemFileWriter.ToText(item, "\n");
        Assert.True(text.IndexOf("first", StringComparison.Ordinal) < text.IndexOf("last", StringComparison.Ordinal));
        Assert.DoesNotContain("duplicate", text);
    }

    [Fact]
    public void Round_trips_through_the_reader()
    {
        var item = Item(Field("00000000-0000-0000-0000-000000000001", "line one\r\nline two"));
        var en = item.Languages.GetOrAdd("en");
        en.UnversionedFields.Add(Field("00000000-0000-0000-0000-000000000002", "display", "__Display name"));
        en.Versions.GetOrAdd(1).Fields.Add(Field("00000000-0000-0000-0000-000000000003", "v1"));
        item.Languages.GetOrAdd("de-DE").Versions.GetOrAdd(2);
        var bytes = ItemFileWriter.ToBytes(item, "\r\n");
        var read = ItemFileReader.Read(bytes);
        Assert.Equal(bytes, ItemFileWriter.ToBytes(read, "\r\n"));
        Assert.Equal("line one\r\nline two", read.SharedFields.Single().Value);
    }

    [Fact]
    public void Block_lines_are_joined_with_CRLF_even_when_the_file_uses_LF()
    {
        var text = "---\nID: \"11111111-2222-3333-4444-555555555555\"\nParent: \"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"\nTemplate: \"76036f5e-cbce-46d1-af0a-4143f9b557aa\"\nPath: /x\nSharedFields:\n- ID: \"00000000-0000-0000-0000-000000000001\"\n  Value: |\n    a\n    b\n";
        Assert.Equal("a\r\nb", ItemFileReader.Read(text).SharedFields.Single().Value);
    }

    [Fact]
    public void Deviation_D1_reads_a_block_whose_blank_line_an_editor_trimmed()
    {
        var text = "---\nID: \"11111111-2222-3333-4444-555555555555\"\nParent: \"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"\nTemplate: \"76036f5e-cbce-46d1-af0a-4143f9b557aa\"\nPath: /x\nSharedFields:\n- ID: \"00000000-0000-0000-0000-000000000001\"\n  Value: |\n    a\n\n    b\n";
        Assert.Equal("a\r\n\r\nb", ItemFileReader.Read(text, lenient: true).SharedFields.Single().Value);
        Assert.Throws<ItemFileFormatException>(() => ItemFileReader.Read(text, lenient: false));
    }

    [Fact]
    public void A_trimmed_trailing_blank_line_ends_the_block_exactly_as_the_Official_CLI_does()
    {
        var text = "---\nID: \"11111111-2222-3333-4444-555555555555\"\nParent: \"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"\nTemplate: \"76036f5e-cbce-46d1-af0a-4143f9b557aa\"\nPath: /x\nSharedFields:\n- ID: \"00000000-0000-0000-0000-000000000001\"\n  Value: |\n    a\n\n- ID: \"00000000-0000-0000-0000-000000000002\"\n  Value: b\n";
        var item = ItemFileReader.Read(text, lenient: true);
        Assert.Equal("a", item.SharedFields.First().Value);
        Assert.Equal(2, item.SharedFields.Count);
    }

    [Fact]
    public void Skips_comments_and_reads_DB_only_after_Path()
    {
        var text = "---\n# a comment\nID: \"11111111-2222-3333-4444-555555555555\"\nParent: \"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"\nTemplate: \"76036f5e-cbce-46d1-af0a-4143f9b557aa\"\nPath: /x\nDB: core\n";
        Assert.Equal("core", ItemFileReader.Read(text).Database);
    }
}
