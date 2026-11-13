using System.Net;
using System.Text.Json;

using Tern.Core.Configuration;
using Tern.Core.Items;
using Tern.Core.Planning;
using Tern.Core.Tests.Cloud;
using Tern.Remote.Auth;
using Tern.Remote.Login;
using Tern.Remote.Management;

namespace Tern.Core.Tests.Remote;

/// <summary>A <see cref="SerializationApi"/> whose CM is a <see cref="FakeHandler"/>, logged in through a throwaway login file.</summary>
internal static class FakeCm
{
    public static SerializationApi Api(string store, FakeHandler handler)
    {
        var endpoint = LoginStore.Load(store).GetOrAdd("cm");
        endpoint.Host = "https://cm.test";
        endpoint.AccessToken = "token";
        endpoint.ExpiresIn = 3600;
        endpoint.LastUpdated = DateTimeOffset.UtcNow;
        return new SerializationApi(new ManagementClient(new HttpClient(handler), "https://cm.test", new TokenProvider(endpoint, new OidcClient(new HttpClient(handler)))));
    }

    /// <summary>A <c>serialize { data }</c> entry for an item, as the CM sends it.</summary>
    public static string Entry(Guid id, string path) =>
        $$$"""[{"data":{"id":"{{{id}}}","parentId":"3d6658d8-a0bf-4e75-b3e2-d050fabcf4e1","templateId":"fe5dd826-48c6-436d-b87a-7c4210c7413b","path":"{{{path}}}"}}]""";
}

/// <summary>
/// An item the CM answered <c>data: null</c> for, in a request it shared with other items, is
/// asked for again on its own: the CM can return it whole then.
/// </summary>
public sealed class SerializeDataRetryTests : IDisposable
{
    private readonly string _store = Path.Join(Path.GetTempPath(), $"tern-cm-{Guid.NewGuid():N}.json");
    private readonly FakeHandler _handler = new();

    public void Dispose() => File.Delete(_store);

    private int Aliases(int request) => _handler.Requests[request].Json.GetProperty("variables").EnumerateObject().Count(p => p.Name.EndsWith('P'));

    [Fact]
    public async Task An_item_with_null_data_in_a_shared_request_is_asked_for_again_alone()
    {
        var small = Guid.NewGuid();
        var large = Guid.NewGuid();
        _handler.Respond(HttpStatusCode.OK, $$$"""{"data":{"a0":{{{FakeCm.Entry(small, "/sitecore/media library/Site A/Small Image")}}},"a1":[{"data":null}]}}""");
        _handler.Respond(HttpStatusCode.OK, $$$"""{"data":{"a0":{{{FakeCm.Entry(large, "/sitecore/media library/Site A/Large Movie")}}}}}""");

        var items = await FakeCm.Api(_store, _handler).GetItemDataAsync("master", [small, large], [], CancellationToken.None);

        Assert.Equal(new[] { small, large }.Order(), items.Keys.Order());
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal(2, Aliases(0));
        Assert.Equal(1, Aliases(1));
        Assert.Equal(large.ToString("D"), _handler.Requests[1].Json.GetProperty("variables").GetProperty("a0P").GetString());
    }

    [Fact]
    public async Task An_item_still_null_alone_is_left_out_and_a_lone_request_is_not_repeated()
    {
        var small = Guid.NewGuid();
        var large = Guid.NewGuid();
        _handler.Respond(HttpStatusCode.OK, $$$"""{"data":{"a0":{{{FakeCm.Entry(small, "/sitecore/media library/Site A/Small Image")}}},"a1":[{"data":null}]}}""");
        _handler.Respond(HttpStatusCode.OK, """{"data":{"a0":[{"data":null}]}}""");
        _handler.Respond(HttpStatusCode.OK, """{"data":{"a0":[{"data":null}]}}""");
        var api = FakeCm.Api(_store, _handler);

        var items = await api.GetItemDataAsync("master", [small, large], [], CancellationToken.None);
        Assert.Equal([small], items.Keys);
        Assert.Equal(2, _handler.Requests.Count);

        // Asked for alone in the first place, it isn't asked for again.
        Assert.Empty(await api.GetItemDataAsync("master", [large], [], CancellationToken.None));
        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task Streaming_retries_too()
    {
        var small = Guid.NewGuid();
        var large = Guid.NewGuid();
        _handler.Respond(HttpStatusCode.OK, $$$"""{"data":{"a0":[{"data":null}],"a1":{{{FakeCm.Entry(small, "/sitecore/media library/Site A/Small Image")}}}}}""");
        _handler.Respond(HttpStatusCode.OK, $$$"""{"data":{"a0":{{{FakeCm.Entry(large, "/sitecore/media library/Site A/Large Movie")}}}}}""");
        var seen = new List<Guid>();

        await FakeCm.Api(_store, _handler).StreamItemDataAsync("master", [large, small], [], batch =>
        {
            lock (seen)
            {
                seen.AddRange(batch.Select(i => i.Id));
            }
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(new[] { small, large }.Order(), seen.Order());
    }
}

/// <summary>
/// A push whose source listed an item but returned no data for it, as Management Services does
/// for a media item with a very large blob, skips that item with why, instead of stopping.
/// </summary>
public sealed class CmApplierSkipTests : IDisposable
{
    private static readonly Guid MediaLibrary = Guid.Parse("3d6658d8-a0bf-4e75-b3e2-d050fabcf4e1");
    private static readonly Guid MediaFolderTemplate = Guid.Parse("fe5dd826-48c6-436d-b87a-7c4210c7413b");
    private static readonly Guid Blob = Guid.Parse("40e50ed9-ba07-4702-992e-a912738d32dc");

    private readonly string _store = Path.Join(Path.GetTempPath(), $"tern-cm-{Guid.NewGuid():N}.json");
    private readonly FakeHandler _handler = new();
    private readonly CmApplier _applier;
    private readonly Include _include = new() { Name = "media", Path = ItemPath.Parse("/sitecore/media library/Site A") };
    private readonly List<string> _log = [];

    public CmApplierSkipTests()
    {
        _applier = new CmApplier(FakeCm.Api(_store, _handler), [], batchSize: 50, _log.Add);
    }

    public void Dispose() => File.Delete(_store);

    private static ItemData Item(string path, Guid parent) => new() { Id = Guid.NewGuid(), ParentId = parent, TemplateId = MediaFolderTemplate, Path = path };

    private static PlanOperation Create(ItemData item) => new() { Action = PlanAction.Create, Module = "Test", Include = "media", Database = "master", Id = item.Id, Path = item.Path, ParentId = item.ParentId };

    private static readonly string Succeeded = """{"data":{"executeSerializationCommands":[{"name":"x","success":true,"messages":[]}]}}""";

    private List<Guid> SentIds() => _handler.Requests
        .Select(r => r.Json)
        .Where(j => j.GetProperty("query").GetString()!.Contains("executeSerializationCommands", StringComparison.Ordinal))
        .SelectMany(j => j.GetProperty("variables").GetProperty("commands").EnumerateArray())
        .Select(c => c.GetProperty("itemID").GetGuid())
        .ToList();

    [Fact]
    public async Task Deviation_D14_an_item_with_no_source_data_is_skipped_with_its_children_and_the_rest_is_sent()
    {
        var folder = Item("/sitecore/media library/Site A", MediaLibrary);
        var large = Item("/sitecore/media library/Site A/Large Movie", folder.Id);
        var underLarge = Item("/sitecore/media library/Site A/Large Movie/Child", large.Id);
        var sibling = Item("/sitecore/media library/Site A/Small Image", folder.Id);
        _handler.Respond(HttpStatusCode.OK, Succeeded);

        var applied = await _applier.ApplyAsync(_include, [Create(folder), Create(large), Create(underLarge), Create(sibling)],
            new Dictionary<Guid, ItemData> { [folder.Id] = folder, [underLarge.Id] = underLarge, [sibling.Id] = sibling }, checkPreconditions: false, CancellationToken.None);

        Assert.Equal([folder.Id, sibling.Id], SentIds());
        Assert.Equal(4, applied.Count);
        var skipped = applied.Where(a => a.Skipped is not null).ToDictionary(a => a.Operation.Id, a => a.Skipped!);
        Assert.Equal(new[] { large.Id, underLarge.Id }.Order(), skipped.Keys.Order());
        Assert.Contains("No source data was fetched for /sitecore/media library/Site A/Large Movie", skipped[large.Id], StringComparison.Ordinal);
        Assert.Contains("very large blob", skipped[large.Id], StringComparison.Ordinal);
        Assert.Contains("its parent", skipped[underLarge.Id], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_update_the_target_returns_no_data_for_is_skipped()
    {
        var item = Item("/sitecore/media library/Site A/Large Movie", MediaLibrary);
        var update = Create(item) with { Action = PlanAction.Update, TargetPath = item.Path };
        // The target's data read answers data: null, as the CM does for an item it cannot serialize.
        _handler.Respond(HttpStatusCode.OK, """{"data":{"a0":[{"data":null}]}}""");

        var applied = await _applier.ApplyAsync(_include, [update], new Dictionary<Guid, ItemData> { [item.Id] = item }, checkPreconditions: false, CancellationToken.None);

        Assert.Empty(SentIds());
        var only = Assert.Single(applied);
        Assert.Contains("from https://cm.test, so it was not updated", only.Skipped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_longer_than_one_JSON_string_is_sent_whole()
    {
        // As the Official CLI sends it: a media item whose base64 blob is more than one
        // Utf8JsonWriter call can write, which 1.0.2 crashed on.
        var large = Item("/sitecore/media library/Site A/Large Movie", MediaLibrary);
        large.SharedFields.Add(new FieldValue(Blob, "Blob", null, new string('A', ItemDataJson.MaxStringLength + 1)));
        var small = Item("/sitecore/media library/Site A/Small Image", MediaLibrary);
        _handler.Respond(HttpStatusCode.OK, Succeeded);
        _handler.Respond(HttpStatusCode.OK, Succeeded);

        var applied = await _applier.ApplyAsync(_include, [Create(large), Create(small)],
            new Dictionary<Guid, ItemData> { [large.Id] = large, [small.Id] = small }, checkPreconditions: false, CancellationToken.None);

        Assert.Equal([large.Id, small.Id], SentIds());
        Assert.All(applied, a => Assert.Null(a.Skipped));
        Assert.True(_handler.Requests[0].Body.Length > ItemDataJson.MaxStringLength);
    }

    [Theory]
    [InlineData("plain base64 text+/=")]
    [InlineData("quotes \" and \\ backslashes, line\r\nbreaks")]
    [InlineData("pairs 😀😀😀😀😀😀 and é")]
    public void A_value_longer_than_one_call_is_written_in_segments_and_reads_back_whole(string value)
    {
        string Write(int max)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                ItemDataJson.WriteString(w, "value", value, max);
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        // Every segment boundary, including one that would split a surrogate pair.
        for (var max = 1; max < value.Length; max++)
        {
            var json = Write(max);
            Assert.Equal(value, JsonElement.Parse(json).GetProperty("value").GetString());
            Assert.Equal(Write(int.MaxValue), json);
        }
    }
}
