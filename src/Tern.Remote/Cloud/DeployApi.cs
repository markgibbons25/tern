using System.Net;
using System.Text.Json;

namespace Tern.Remote.Cloud;

/// <summary>
/// SitecoreAI's Deploy API: organisations, projects, environments, variables and deployments
/// (docs/protocol/deploy-api.md). Responses are returned as JSON elements because the commands
/// print them field by field; the few values a command acts on are read with <see cref="CloudJson"/>.
/// </summary>
public sealed class DeployApi(CloudClient client)
{
    public CloudClient Client => client;

    // Projects

    public Task<JsonElement> ProjectsAsync(CancellationToken ct) => client.GetJsonAsync("/api/projects/v1", ct);

    public Task<JsonElement> ProjectAsync(string projectId, CancellationToken ct) =>
        Map(() => client.GetJsonAsync($"/api/projects/v1/{Seg(projectId)}", ct), (HttpStatusCode.NotFound, $"No project was found with the ID '{projectId}'."));

    public Task<JsonElement> CreateProjectAsync(string name, CancellationToken ct) =>
        Map(async () => CloudJson.WithLocation(await client.PostJsonAsync("/api/projects/v1", w => w.WriteString("name", name), ct)),
            (HttpStatusCode.Conflict, $"A project named '{name}' already exists in the organization."),
            (HttpStatusCode.PaymentRequired, "The organization has reached the number of projects its subscription allows."),
            (HttpStatusCode.Gone, "Projects can no longer be created this way. Create it in the SitecoreAI Deploy app."));

    public Task UpdateProjectAsync(string projectId, string name, CancellationToken ct) =>
        Map(() => client.PutJsonAsync($"/api/projects/v1/{Seg(projectId)}", w => w.WriteString("name", name), ct),
            (HttpStatusCode.NotFound, $"No project was found with the ID '{projectId}'."),
            (HttpStatusCode.Conflict, $"A project named '{name}' already exists in the organization."));

    public Task DeleteProjectAsync(string projectId, CancellationToken ct) =>
        Map(() => client.DeleteAsync($"/api/projects/v1/{Seg(projectId)}", ct),
            (HttpStatusCode.NotFound, $"No project was found with the ID '{projectId}'."),
            (HttpStatusCode.Conflict, "The project still has environments; delete them first."));

    // Environments

    public Task<JsonElement> EnvironmentsAsync(string projectId, CancellationToken ct) =>
        Map(() => client.GetJsonAsync($"/api/projects/v2/{Seg(projectId)}/environments", ct), (HttpStatusCode.NotFound, $"No project was found with the ID '{projectId}'."));

    public Task<JsonElement> EnvironmentAsync(string environmentId, CancellationToken ct) =>
        Map(() => client.GetJsonAsync($"/api/environments/v2/{Seg(environmentId)}", ct), (HttpStatusCode.NotFound, $"No environment was found with the ID '{environmentId}'."));

    /// <summary>
    /// Creates an environment. <paramref name="type"/> is <c>null</c> (CM and editing host),
    /// <c>"cm"</c> or <c>"eh"</c>; an editing host names the CM environment it belongs to.
    /// The tenant type is sent as a number (0 non-production, 1 production), as the Official CLI sends it.
    /// </summary>
    public Task<JsonElement> CreateEnvironmentAsync(string projectId, string name, bool production, string? type, string? cmEnvironmentId, CancellationToken ct) =>
        Map(async () => CloudJson.WithLocation(await client.PostJsonAsync($"/api/projects/v2/{Seg(projectId)}/environments", w =>
            {
                w.WriteString("name", name);
                w.WriteNumber("tenantType", production ? 1 : 0);
                w.WriteString("type", type);
                if (cmEnvironmentId is not null)
                {
                    w.WriteStartObject("EditingHostEnvironmentDetails");
                    w.WriteString("CmEnvironmentId", cmEnvironmentId);
                    w.WriteEndObject();
                }
            }, ct)),
            (HttpStatusCode.NotFound, $"No project was found with the ID '{projectId}'."),
            (HttpStatusCode.Conflict, $"An environment named '{name}' already exists in project '{projectId}'."),
            (HttpStatusCode.PaymentRequired, $"Project '{projectId}' has reached the number of environments its subscription allows."));

    public Task UpdateEnvironmentAsync(string environmentId, string name, CancellationToken ct) =>
        Map(() => client.PutJsonAsync($"/api/environments/v1/{Seg(environmentId)}", w => w.WriteString("name", name), ct),
            (HttpStatusCode.NotFound, $"No environment was found with the ID '{environmentId}'."),
            (HttpStatusCode.Conflict, $"An environment named '{name}' already exists in the project."));

    public Task DeleteEnvironmentAsync(string environmentId, CancellationToken ct) =>
        Map(() => client.DeleteAsync($"/api/environments/v1/{Seg(environmentId)}", ct),
            (HttpStatusCode.NotFound, $"No environment was found with the ID '{environmentId}'."),
            (HttpStatusCode.Conflict, "The environment cannot be deleted while something depends on it, such as a running deployment or an editing host."));

    public Task RestartEnvironmentAsync(string environmentId, CancellationToken ct) =>
        Map(() => client.PostAsync($"/api/environments/v1/{Seg(environmentId)}/restart", ct), (HttpStatusCode.NotFound, $"No environment was found with the ID '{environmentId}'."));

    public Task<JsonElement> RestartStatusAsync(string environmentId, CancellationToken ct) =>
        client.GetJsonAsync($"/api/environments/v1/{Seg(environmentId)}/restart", ct);

    /// <summary>Promotes a deployment to another environment, and returns the new deployment's ID.</summary>
    public async Task<string> PromoteAsync(string environmentId, string deploymentId, CancellationToken ct)
    {
        try
        {
            var response = await client.PostJsonAsync($"/api/environments/v2/{Seg(environmentId)}/promote/{Seg(deploymentId)}", w =>
            {
                w.WriteString("environmentId", environmentId);
                w.WriteString("deploymentId", deploymentId);
            }, ct);
            return response.CreatedId ?? throw new CloudApiException("The promotion was accepted, but SitecoreAI did not say which deployment it created.");
        }
        catch (CloudApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            var text = ex.StatusCode == HttpStatusCode.NotFound ? ex.Problem("title") : ex.Problem("detail");
            throw new CloudApiException(text ?? ex.Message, ex.StatusCode, ex.Body, ex);
        }
    }

    /// <summary>Reads <c>/healthz/ready</c> on an environment's CM, with the SitecoreAI token.</summary>
    public async Task<string> CmHealthAsync(string cmHost, CancellationToken ct)
    {
        var url = CmUrl(cmHost, "/healthz/ready");
        try
        {
            return (await client.SendCheckedAsync(HttpMethod.Get, url.AbsoluteUri, null, ct)).Text;
        }
        catch (CloudApiException ex) when (ex.StatusCode is not null)
        {
            throw new CloudApiException(ex.StatusCode == HttpStatusCode.InternalServerError
                ? $"The CM at {url.Host} is not available."
                : $"Could not read the health of the CM at {url.Host}: {ex.Message}", ex.StatusCode, ex.Body, ex);
        }
    }

    /// <summary>
    /// Waits for a CM's host name to answer after a deployment. Any HTTP answer counts, including a
    /// redirect to the login page.
    /// </summary>
    public async Task<bool> WaitForCmAsync(string cmHost, TimeSpan timeout, Func<TimeSpan, CancellationToken, Task>? delay, CancellationToken ct)
    {
        delay ??= Task.Delay;
        var url = CmUrl(cmHost, "/sitecore");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var response = await client.SendAsync(HttpMethod.Head, url.AbsoluteUri, null, TimeSpan.FromSeconds(30), ct);
                if ((int)response.Status < 400)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Not answering yet, as expected while it starts: try again until the deadline.
            }
            await delay(TimeSpan.FromSeconds(1), ct);
        }
        return false;
    }

    /// <summary><c>https://&lt;host&gt;&lt;path&gt;</c> for a CM host as the Deploy API reports it (a bare host name).</summary>
    public static Uri CmUrl(string cmHost, string path) => new UriBuilder(cmHost.Contains("://", StringComparison.Ordinal) ? cmHost.TrimEnd('/') + path : "https://" + cmHost.TrimEnd('/') + path)
    {
        Scheme = Uri.UriSchemeHttps,
        Port = -1,
    }.Uri;

    // Variables

    public Task<JsonElement> VariablesAsync(string environmentId, CancellationToken ct) =>
        Map(() => client.GetJsonAsync($"/api/environments/v1/{Seg(environmentId)}/variables", ct), (HttpStatusCode.NotFound, $"No environment was found with the ID '{environmentId}'."));

    public Task UpsertVariableAsync(string environmentId, string name, string value, bool secret, string? target, CancellationToken ct) =>
        Map(() => client.PostJsonAsync($"/api/environments/v1/{Seg(environmentId)}/variables/{Seg(name)}", w =>
            {
                w.WriteBoolean("secret", secret);
                w.WriteString("value", value);
                w.WriteString("target", target);
            }, ct),
            (HttpStatusCode.NotFound, $"No environment was found with the ID '{environmentId}'."),
            (HttpStatusCode.Conflict, $"Variable '{name}' already exists with a different secret setting; delete it before changing whether it is secret."));

    public Task DeleteVariableAsync(string environmentId, string name, CancellationToken ct) =>
        Map(() => client.DeleteAsync($"/api/environments/v1/{Seg(environmentId)}/variables/{Seg(name)}", ct), (HttpStatusCode.NotFound, $"No environment was found with the ID '{environmentId}'."));

    // Deployments

    /// <summary>Creates a deployment and returns its ID.</summary>
    public async Task<string> CreateDeploymentAsync(string environmentId, CancellationToken ct)
    {
        try
        {
            var response = await client.PostJsonAsync($"/api/environments/v2/{Seg(environmentId)}/deployments", w => w.WriteString("environmentId", environmentId), ct);
            return response.CreatedId ?? throw new CloudApiException("The deployment was created, but SitecoreAI did not return its ID.");
        }
        catch (CloudApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
        {
            throw new CloudApiException($"No environment was found with the ID '{environmentId}'.", ex.StatusCode, ex.Body, ex);
        }
        catch (CloudApiException ex) when (ex.StatusCode is HttpStatusCode.Conflict)
        {
            throw Conflict(ex, environmentId);
        }
    }

    public Task<JsonElement> DeploymentAsync(string deploymentId, CancellationToken ct) =>
        Map(() => client.GetJsonAsync($"/api/deployments/v3/{Seg(deploymentId)}", ct), (HttpStatusCode.NotFound, $"No deployment was found with the ID '{deploymentId}'."));

    public Task<JsonElement> DeploymentsAsync(string environmentId, CancellationToken ct) =>
        Map(() => client.GetJsonAsync($"/api/environments/v2/{Seg(environmentId)}/deployments", ct), (HttpStatusCode.NotFound, $"No environment was found with the ID '{environmentId}'."));

    /// <summary>Uploads the zipped source of a deployment that has not started yet.</summary>
    public Task UploadSourceAsync(string deploymentId, byte[] zip, TimeSpan? timeout, CancellationToken ct) =>
        Map(() => client.SendCheckedAsync(HttpMethod.Post, $"/api/deployments/v2/{Seg(deploymentId)}/source", () =>
            {
                var content = new ByteArrayContent(zip);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                return content;
            }, ct, timeout),
            (HttpStatusCode.NotFound, $"No deployment was found with the ID '{deploymentId}'."),
            (HttpStatusCode.Conflict, "The deployment has already started, so its source can no longer be uploaded."),
            (HttpStatusCode.UnsupportedMediaType, "SitecoreAI did not accept the uploaded archive."),
            (HttpStatusCode.RequestEntityTooLarge, "The source archive is too large to upload. Add the folders the build does not need to .gitignore."));

    public async Task StartDeploymentAsync(string deploymentId, CancellationToken ct)
    {
        try
        {
            await client.PostAsync($"/api/deployments/v1/{Seg(deploymentId)}/deploy", ct);
        }
        catch (CloudApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
        {
            throw new CloudApiException($"No deployment was found with the ID '{deploymentId}'.", ex.StatusCode, ex.Body, ex);
        }
        catch (CloudApiException ex) when (ex.StatusCode is HttpStatusCode.Conflict)
        {
            string? environmentId = null;
            try
            {
                environmentId = CloudJson.String(await DeploymentAsync(deploymentId, ct), "environmentId");
            }
            catch (CloudApiException)
            {
                // The environment only makes the message more specific; report the conflict without it.
            }
            throw Conflict(ex, environmentId);
        }
    }

    public Task CancelDeploymentAsync(string deploymentId, CancellationToken ct) =>
        Map(() => client.PostAsync($"/api/deployments/v1/{Seg(deploymentId)}/cancel", ct), (HttpStatusCode.NotFound, $"No deployment was found with the ID '{deploymentId}'."));

    private static CloudApiException Conflict(CloudApiException ex, string? environmentId)
    {
        var running = ex.Problem("instance")?.TrimEnd('/').Split('/').Last();
        var detail = ex.Problem("detail") ?? "Another deployment is in progress.";
        var where = environmentId is null ? string.Empty : $" in environment '{environmentId}'";
        return new CloudApiException(running is null ? $"{detail}{where}" : $"{detail} Deployment '{running}' is still running{where}.", ex.StatusCode, ex.Body, ex);
    }

    // Organization

    public Task<JsonElement> OrganizationAsync(string organizationId, CancellationToken ct) =>
        client.GetJsonAsync($"/api/organizations/v4/{Seg(organizationId)}", ct);

    public Task<JsonElement> OrganizationHealthAsync(string organizationId, CancellationToken ct) =>
        client.GetJsonAsync($"/api/organizations/v4/{Seg(organizationId)}/health", ct);

    public async Task<byte[]> LicenseAsync(string organizationId, CancellationToken ct) =>
        (await Map(() => client.SendCheckedAsync(HttpMethod.Get, $"/api/organizations/v1/{Seg(organizationId)}/license", null, ct),
            (HttpStatusCode.NotFound, "The organization has no license available to download."))).Body;

    /// <summary>The Deploy API's own health, which the Official CLI checks alongside every command.</summary>
    public Task<JsonElement> ServiceHealthAsync(CancellationToken ct) => client.GetJsonAsync("/health/components", ct);

    internal static string Seg(string value) => Uri.EscapeDataString(value);

    internal static async Task<T> Map<T>(Func<Task<T>> call, params (HttpStatusCode Status, string Message)[] messages)
    {
        try
        {
            return await call();
        }
        catch (CloudApiException ex) when (ex.StatusCode is { } s && messages.Any(m => m.Status == s))
        {
            throw new CloudApiException(messages.First(m => m.Status == s).Message, ex.StatusCode, ex.Body, ex);
        }
    }

    internal static async Task Map(Func<Task> call, params (HttpStatusCode Status, string Message)[] messages) =>
        await Map(async () =>
        {
            await call();
            return true;
        }, messages);
}
