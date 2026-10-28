using System.Net;
using System.Text;
using System.Text.Json;

using Tern.Remote.Auth;
using Tern.Remote.Cloud;
using Tern.Remote.Login;

namespace Tern.Core.Tests.Cloud;

/// <summary>Records every request and answers from a script, so request building can be checked without SitecoreAI.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<Recorded> Requests { get; } = [];

    public sealed record Recorded(HttpMethod Method, string Url, string? Authorization, string? ContentType, byte[] Body)
    {
        public string Text => Encoding.UTF8.GetString(Body);
        public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
    }

    public FakeHandler Respond(HttpStatusCode status, string? body = null, string? location = null)
    {
        _responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json") };
            if (location is not null)
            {
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            }
            return response;
        });
        return this;
    }

    public FakeHandler Respond(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _responses.Enqueue(respond);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add(new Recorded(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType, body));
        var response = _responses.Count > 0 ? _responses.Dequeue()(request) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        response.RequestMessage = request;
        return response;
    }
}

internal sealed class CloudFixture : IDisposable
{
    public CloudFixture()
    {
        StorePath = Path.Join(Path.GetTempPath(), $"tern-cloud-{Guid.NewGuid():N}.json");
        Store = LoginStore.Load(StorePath);
        Endpoint = Store.GetOrAdd("xmCloud");
        Endpoint.Host = "https://deploy.test/";
        Endpoint.Authority = "https://auth.test/";
        Endpoint.ClientId = "client";
        Endpoint.AccessToken = "token-1";
        Endpoint.RefreshToken = "refresh-1";
        Endpoint.ExpiresIn = 3600;
        Endpoint.LastUpdated = DateTimeOffset.UtcNow;
        Endpoint.SetVariable("xmCloudMonitoringHost", "https://monitoring.test/");
        var tokens = new TokenProvider(Endpoint, new OidcClient(new HttpClient(Handler)));
        Client = new CloudClient(new HttpClient(Handler), new Uri("https://deploy.test/"), tokens, (wait, _) =>
        {
            Waits.Add(wait);
            return Task.CompletedTask;
        });
        Deploy = new DeployApi(Client);
        Monitoring = new MonitoringApi(new CloudClient(new HttpClient(Handler), CloudEndpoints.MonitoringHost(Endpoint), tokens, (_, _) => Task.CompletedTask));
    }

    public string StorePath { get; }
    public LoginStore Store { get; }
    public Endpoint Endpoint { get; }
    public FakeHandler Handler { get; } = new();
    public List<TimeSpan> Waits { get; } = [];
    public CloudClient Client { get; }
    public DeployApi Deploy { get; }
    public MonitoringApi Monitoring { get; }

    public FakeHandler.Recorded Only => Assert.Single(Handler.Requests);

    public void Dispose() => File.Delete(StorePath);
}

public class DeployApiRequestTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Creates_a_project_and_merges_the_location_into_the_result()
    {
        using var f = new CloudFixture();
        f.Handler.Respond(HttpStatusCode.Created, """{"id":"p1","name":"Shop","region":"aue"}""", "https://deploy.test/api/projects/v1/p1");
        var project = await f.Deploy.CreateProjectAsync("Shop", Ct);

        var request = f.Only;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://deploy.test/api/projects/v1", request.Url);
        Assert.Equal("Bearer token-1", request.Authorization);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("""{"name":"Shop"}""", request.Text);
        Assert.Equal("https://deploy.test/api/projects/v1/p1", CloudJson.String(project, "location"));
        Assert.Equal("p1", CloudJson.String(project, "id"));
    }

    [Fact]
    public async Task Renames_and_deletes_a_project()
    {
        using var f = new CloudFixture();
        await f.Deploy.UpdateProjectAsync("p1", "New name", Ct);
        await f.Deploy.DeleteProjectAsync("p1", Ct);

        Assert.Equal(HttpMethod.Put, f.Handler.Requests[0].Method);
        Assert.Equal("https://deploy.test/api/projects/v1/p1", f.Handler.Requests[0].Url);
        Assert.Equal("""{"name":"New name"}""", f.Handler.Requests[0].Text);
        Assert.Equal(HttpMethod.Delete, f.Handler.Requests[1].Method);
        Assert.Equal("https://deploy.test/api/projects/v1/p1", f.Handler.Requests[1].Url);
        Assert.Empty(f.Handler.Requests[1].Body);
    }

    [Theory]
    [InlineData(false, null, """{"name":"dev","tenantType":0,"type":null}""")]
    [InlineData(true, "cm", """{"name":"dev","tenantType":1,"type":"cm"}""")]
    public async Task Creates_an_environment_with_a_numeric_tenant_type(bool production, string? type, string expected)
    {
        using var f = new CloudFixture();
        f.Handler.Respond(HttpStatusCode.Created, """{"id":"e1"}""", "/api/environments/v2/e1");
        await f.Deploy.CreateEnvironmentAsync("p1", "dev", production, type, null, Ct);

        Assert.Equal(HttpMethod.Post, f.Only.Method);
        Assert.Equal("https://deploy.test/api/projects/v2/p1/environments", f.Only.Url);
        Assert.Equal(expected, f.Only.Text);
    }

    [Fact]
    public async Task Creates_an_editing_host_that_names_its_CM()
    {
        using var f = new CloudFixture();
        await f.Deploy.CreateEnvironmentAsync("p1", "eh", false, "eh", "cm1", Ct);
        Assert.Equal("""{"name":"eh","tenantType":0,"type":"eh","EditingHostEnvironmentDetails":{"CmEnvironmentId":"cm1"}}""", f.Only.Text);
    }

    [Fact]
    public async Task Updates_deletes_and_restarts_an_environment()
    {
        using var f = new CloudFixture();
        await f.Deploy.UpdateEnvironmentAsync("e1", "renamed", Ct);
        await f.Deploy.DeleteEnvironmentAsync("e1", Ct);
        await f.Deploy.RestartEnvironmentAsync("e1", Ct);

        var r = f.Handler.Requests;
        Assert.Equal((HttpMethod.Put, "https://deploy.test/api/environments/v1/e1", """{"name":"renamed"}"""), (r[0].Method, r[0].Url, r[0].Text));
        Assert.Equal((HttpMethod.Delete, "https://deploy.test/api/environments/v1/e1"), (r[1].Method, r[1].Url));
        Assert.Equal((HttpMethod.Post, "https://deploy.test/api/environments/v1/e1/restart"), (r[2].Method, r[2].Url));
        Assert.Empty(r[2].Body);
    }

    [Fact]
    public async Task Upserts_and_deletes_a_variable()
    {
        using var f = new CloudFixture();
        await f.Deploy.UpsertVariableAsync("e1", "SITECORE_X", "value", secret: true, target: null, Ct);
        await f.Deploy.UpsertVariableAsync("e1", "HOST_Y", "v2", secret: false, target: "CM", Ct);
        await f.Deploy.DeleteVariableAsync("e1", "SITECORE_X", Ct);

        var r = f.Handler.Requests;
        Assert.Equal((HttpMethod.Post, "https://deploy.test/api/environments/v1/e1/variables/SITECORE_X"), (r[0].Method, r[0].Url));
        Assert.Equal("""{"secret":true,"value":"value","target":null}""", r[0].Text);
        Assert.Equal("""{"secret":false,"value":"v2","target":"CM"}""", r[1].Text);
        Assert.Equal((HttpMethod.Delete, "https://deploy.test/api/environments/v1/e1/variables/SITECORE_X"), (r[2].Method, r[2].Url));
    }

    [Fact]
    public async Task Promotes_a_deployment_and_returns_the_new_one()
    {
        using var f = new CloudFixture();
        f.Handler.Respond(HttpStatusCode.Accepted, null, "https://deploy.test/api/deployments/v3/new1");
        var id = await f.Deploy.PromoteAsync("e2", "d1", Ct);

        Assert.Equal("new1", id);
        Assert.Equal((HttpMethod.Post, "https://deploy.test/api/environments/v2/e2/promote/d1"), (f.Only.Method, f.Only.Url));
        Assert.Equal("""{"environmentId":"e2","deploymentId":"d1"}""", f.Only.Text);
    }

    [Fact]
    public async Task Creates_uploads_starts_and_cancels_a_deployment()
    {
        using var f = new CloudFixture();
        f.Handler.Respond(HttpStatusCode.Created, null, "/api/deployments/v1/d9");
        var id = await f.Deploy.CreateDeploymentAsync("e1", Ct);
        byte[] zip = [0x50, 0x4b, 3, 4];
        await f.Deploy.UploadSourceAsync(id, zip, null, Ct);
        await f.Deploy.StartDeploymentAsync(id, Ct);
        await f.Deploy.CancelDeploymentAsync(id, Ct);

        Assert.Equal("d9", id);
        var r = f.Handler.Requests;
        Assert.Equal((HttpMethod.Post, "https://deploy.test/api/environments/v2/e1/deployments", """{"environmentId":"e1"}"""), (r[0].Method, r[0].Url, r[0].Text));
        Assert.Equal((HttpMethod.Post, "https://deploy.test/api/deployments/v2/d9/source", "application/octet-stream"), (r[1].Method, r[1].Url, r[1].ContentType));
        Assert.Equal(zip, r[1].Body);
        Assert.Equal((HttpMethod.Post, "https://deploy.test/api/deployments/v1/d9/deploy"), (r[2].Method, r[2].Url));
        Assert.Equal((HttpMethod.Post, "https://deploy.test/api/deployments/v1/d9/cancel"), (r[3].Method, r[3].Url));
    }

    [Fact]
    public async Task Reads_from_the_deploy_and_monitoring_hosts()
    {
        using var f = new CloudFixture();
        f.Handler.Respond(HttpStatusCode.OK, "[]").Respond(HttpStatusCode.OK, "{}").Respond(HttpStatusCode.OK, "[]").Respond(HttpStatusCode.OK, "log text").Respond(HttpStatusCode.OK, "{}");
        await f.Deploy.DeploymentsAsync("e1", Ct);
        await f.Deploy.OrganizationAsync("org_1", Ct);
        await f.Monitoring.LogFilesAsync("e1", latest: true, Ct);
        var log = await f.Monitoring.LogFileAsync("e1", "Owin.log.txt", download: false, null, Ct);
        await f.Monitoring.DeploymentLogsAsync("d1", Ct);

        Assert.Equal(
        [
            "https://deploy.test/api/environments/v2/e1/deployments",
            "https://deploy.test/api/organizations/v4/org_1",
            "https://monitoring.test/api/logs/v1/e1?latest=True",
            "https://monitoring.test/api/logs/v2/e1/Owin.log.txt?download=False",
            "https://monitoring.test/api/Deployments/v1/d1",
        ], f.Handler.Requests.Select(r => r.Url));
        Assert.Equal("log text", Encoding.UTF8.GetString(log));
    }

    [Fact]
    public async Task Retries_a_429_three_times_with_growing_waits_then_fails()
    {
        using var f = new CloudFixture();
        for (var i = 0; i < 4; i++)
        {
            f.Handler.Respond(HttpStatusCode.TooManyRequests, "{}");
        }
        var ex = await Assert.ThrowsAsync<CloudApiException>(() => f.Deploy.ProjectsAsync(Ct));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(4, f.Handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], f.Waits);
    }

    [Fact]
    public async Task Retries_a_network_error()
    {
        using var f = new CloudFixture();
        f.Handler.Respond(_ => throw new HttpRequestException("connection reset")).Respond(HttpStatusCode.OK, "[]");
        var projects = await f.Deploy.ProjectsAsync(Ct);

        Assert.Equal(JsonValueKind.Array, projects.ValueKind);
        Assert.Equal(2, f.Handler.Requests.Count);
    }

    [Fact]
    public async Task Refreshes_the_token_after_a_401_and_retries()
    {
        using var f = new CloudFixture();
        f.Handler
            .Respond(HttpStatusCode.Unauthorized, "{}")
            .Respond(HttpStatusCode.OK, """{"token_endpoint":"https://auth.test/oauth/token"}""")
            .Respond(HttpStatusCode.OK, """{"access_token":"token-2","refresh_token":"refresh-2","expires_in":86400}""")
            .Respond(HttpStatusCode.OK, "[]");
        await f.Deploy.ProjectsAsync(Ct);

        var r = f.Handler.Requests;
        Assert.Equal("https://auth.test/.well-known/openid-configuration", r[1].Url);
        Assert.Contains("grant_type=refresh_token", r[2].Text);
        Assert.Equal("Bearer token-2", r[3].Authorization);
        Assert.Equal("token-2", f.Endpoint.AccessToken);
    }

    [Fact]
    public async Task Explains_a_missing_project_and_validation_errors()
    {
        using var f = new CloudFixture();
        f.Handler
            .Respond(HttpStatusCode.NotFound, """{"title":"Not Found"}""")
            .Respond(HttpStatusCode.BadRequest, """{"title":"One or more validation errors occurred.","errors":{"Name":["Name is too long."],"Region":["Unknown region."]}}""");

        var missing = await Assert.ThrowsAsync<CloudApiException>(() => f.Deploy.ProjectAsync("nope", Ct));
        var invalid = await Assert.ThrowsAsync<CloudApiException>(() => f.Deploy.CreateProjectAsync(new string('x', 300), Ct));

        Assert.Equal("No project was found with the ID 'nope'.", missing.Message);
        Assert.Equal("Name is too long.; Unknown region.", invalid.Message);
    }

    [Fact]
    public async Task Names_the_running_deployment_on_a_conflict()
    {
        using var f = new CloudFixture();
        f.Handler.Respond(HttpStatusCode.Conflict, """{"detail":"A deployment is already running.","instance":"/api/deployments/v1/run7"}""");
        var ex = await Assert.ThrowsAsync<CloudApiException>(() => f.Deploy.CreateDeploymentAsync("e1", Ct));
        Assert.Contains("run7", ex.Message);
        Assert.Contains("e1", ex.Message);
    }
}

public class CloudEndpointTests
{
    private static string Jwt(string payloadJson) =>
        "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    private static LoginStore Load(string json)
    {
        var file = Path.Join(Path.GetTempPath(), $"tern-user-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, json);
        return LoginStore.Load(file);
    }

    private static CloudLogin Login(string token) => new(
        Defaults.Authority, Defaults.SitecoreAiDeviceClientId, null, false, Defaults.Audience, Defaults.DeployApi, Defaults.MonitoringApi, false,
        new TokenResponse(token, "refresh", 86400));

    [Theory]
    [InlineData("XmCloud", "xmCloud")]
    [InlineData("XmCloudMonitoringHost", "xmCloudMonitoringHost")]
    [InlineData("DEV", "dev")]
    [InlineData("URLValue", "urlValue")]
    [InlineData("dev-site", "dev-site")]
    public void Writes_endpoint_names_the_way_the_Official_CLI_does(string name, string key) =>
        Assert.Equal(key, LoginStore.OfficialKey(name));

    [Fact]
    public void Login_reuses_an_existing_endpoint_and_stores_the_organization()
    {
        var store = Load("""{ "endpoints": { "XMCLOUD": { "clientSecret": "stale", "host": "https://old/" }, "default": { "ref": "xmcloud", "host": "https://cm/" } } }""");
        var endpoint = CloudEndpoints.StoreLogin(store, Login(Jwt("""{"https://auth.sitecorecloud.io/claims/org_id":"org_9"}""")), DateTimeOffset.UtcNow);
        store.Save();

        Assert.Equal("XMCLOUD", endpoint.Name);
        using var doc = JsonDocument.Parse(File.ReadAllText(store.Path));
        var cloud = doc.RootElement.GetProperty("endpoints").GetProperty("XMCLOUD");
        Assert.Equal(
            ["allowWrite", "host", "authority", "accessToken", "refreshToken", "refreshTokenParameters", "expiresIn", "lastUpdated", "clientId", "variables", "audience"],
            cloud.EnumerateObject().Select(p => p.Name));
        Assert.Equal("https://xmclouddeploy-api.sitecorecloud.io/", cloud.GetProperty("host").GetString());
        Assert.Equal("org_9", cloud.GetProperty("refreshTokenParameters").GetProperty("organization_id").GetString());
        Assert.Equal("https://xmcloud-monitoring-api.sitecorecloud.io/", cloud.GetProperty("variables").GetProperty("xmCloudMonitoringHost").GetString());
        Assert.False(cloud.TryGetProperty("clientSecret", out _));
        Assert.Equal(2, doc.RootElement.GetProperty("endpoints").EnumerateObject().Count());
        File.Delete(store.Path);
    }

    [Fact]
    public void Login_with_client_credentials_stores_the_secret_under_a_new_xmCloud_endpoint()
    {
        var store = Load("""{ "endpoints": {} }""");
        var endpoint = CloudEndpoints.StoreLogin(store, Login("not-a-jwt") with { ClientId = "id", ClientSecret = "secret", UseClientCredentials = true }, DateTimeOffset.UtcNow);

        Assert.Equal("xmCloud", endpoint.Name);
        Assert.True(endpoint.UseClientCredentials);
        Assert.Equal("secret", endpoint.ClientSecret);
        Assert.Empty(endpoint.RefreshTokenParameters);
        File.Delete(store.Path);
    }

    [Fact]
    public void Connect_writes_a_ref_endpoint_and_logout_removes_it_with_the_login()
    {
        var store = Load("""{ "endpoints": { "xmCloud": { "accessToken": "t" }, "local": { "host": "https://local/" } } }""");
        var dev = CloudEndpoints.Connect(store, "Dev", CloudEndpoints.CmHostUrl(null, "xmc-dev.sitecorecloud.io"), allowWrite: true);
        store.Save();

        Assert.Equal("dev", dev.Name);
        using (var doc = JsonDocument.Parse(File.ReadAllText(store.Path)))
        {
            var e = doc.RootElement.GetProperty("endpoints").GetProperty("dev");
            Assert.Equal(["ref", "allowWrite", "host", "variables"], e.EnumerateObject().Select(p => p.Name));
            Assert.Equal("XmCloud", e.GetProperty("ref").GetString());
            Assert.Equal("https://xmc-dev.sitecorecloud.io/", e.GetProperty("host").GetString());
        }

        var removed = CloudEndpoints.Logout(store);
        Assert.Equal(["xmCloud", "dev"], removed);
        Assert.Equal(["local"], store.All.Select(e => e.Name));
        File.Delete(store.Path);
    }

    [Theory]
    [InlineData(null, "xmc-a.sitecorecloud.io", "https://xmc-a.sitecorecloud.io/")]
    [InlineData("cm.example.com", "ignored", "https://cm.example.com/")]
    [InlineData("https://cm.example.com:8443", "ignored", "https://cm.example.com:8443/")]
    public void Builds_the_CM_url_for_connect(string? option, string host, string expected) =>
        Assert.Equal(expected, CloudEndpoints.CmHostUrl(option, host));

    [Fact]
    public void Reads_a_claim_from_a_token_without_verifying_it()
    {
        Assert.Equal("org_1", Tern.Remote.Auth.Jwt.Claim(Jwt("""{"https://auth.sitecorecloud.io/claims/org_id":"org_1"}"""), CloudEndpoints.OrganizationClaim));
        Assert.Null(Tern.Remote.Auth.Jwt.Claim("garbage", CloudEndpoints.OrganizationClaim));
    }
}
