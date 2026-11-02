using System.Net;
using System.Text.Json;

using Tern.Core.Tests.Cloud;
using Tern.Remote.Auth;
using Tern.Remote.Login;
using Tern.Remote.Management;

namespace Tern.Core.Tests.Remote;

public class PublishingApiTests : IDisposable
{
    private const string Job = """{"data":{"publish":{"id":"j1","processedCount":0,"stateCode":0,"stateName":"Queued"},"singleItemPublish":{"id":"j2","processedCount":0,"stateCode":0,"stateName":"Queued"}}}""";

    private readonly string _storePath = Path.Join(Path.GetTempPath(), $"tern-publish-{Guid.NewGuid():N}.json");
    private readonly FakeHandler _handler = new();
    private readonly PublishingApi _api;

    public PublishingApiTests()
    {
        var endpoint = LoginStore.Load(_storePath).GetOrAdd("default");
        endpoint.Host = "https://cm.test/";
        endpoint.Authority = "https://auth.test/";
        endpoint.ClientId = "client";
        endpoint.AccessToken = "token-1";
        endpoint.ExpiresIn = 3600;
        endpoint.LastUpdated = DateTimeOffset.UtcNow;
        var tokens = new TokenProvider(endpoint, new OidcClient(new HttpClient(_handler)));
        _api = new PublishingApi(new ManagementClient(new HttpClient(_handler), "https://cm.test", tokens));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        File.Delete(_storePath);
    }

    private JsonElement Variables => Assert.Single(_handler.Requests).Json.GetProperty("variables");

    [Fact]
    public async Task A_publish_without_languages_leaves_them_out_so_every_language_is_published()
    {
        _handler.Respond(HttpStatusCode.OK, Job);
        await _api.PublishAsync(null, [], [], false, null, CancellationToken.None);

        // An empty list publishes no language at all; the Official CLI sends none.
        Assert.False(Variables.TryGetProperty("languages", out _));
        Assert.False(Variables.TryGetProperty("itemIds", out _));
    }

    [Fact]
    public async Task A_publish_sends_the_languages_it_was_given()
    {
        _handler.Respond(HttpStatusCode.OK, Job);
        await _api.PublishAsync(null, ["en", "da"], [], false, null, CancellationToken.None);

        Assert.Equal("""["en","da"]""", Variables.GetProperty("languages").GetRawText());
    }

    [Fact]
    public async Task An_item_publish_without_languages_leaves_them_out()
    {
        _handler.Respond(HttpStatusCode.OK, Job);
        await _api.PublishItemAsync("/sitecore/content/Tenant/Site", [], false, null, false, true, CancellationToken.None);

        Assert.False(Variables.TryGetProperty("languages", out _));
        Assert.Equal("/sitecore/content/Tenant/Site", Variables.GetProperty("path").GetString());
    }
}
