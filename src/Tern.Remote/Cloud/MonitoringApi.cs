using System.Net;
using System.Text.Json;

namespace Tern.Remote.Cloud;

/// <summary>SitecoreAI's Monitoring API: an environment's CM log files and a deployment's build logs.</summary>
public sealed class MonitoringApi(CloudClient client)
{
    public CloudClient Client => client;

    /// <summary>The CM's log files. The flag is sent the way .NET prints a bool (<c>True</c>/<c>False</c>), as the Official CLI sends it.</summary>
    public Task<JsonElement> LogFilesAsync(string environmentId, bool latest, CancellationToken ct) =>
        DeployApi.Map(() => client.GetJsonAsync($"/api/logs/v1/{DeployApi.Seg(environmentId)}?latest={(latest ? "True" : "False")}", ct),
            (HttpStatusCode.NotFound, $"No log files were found for environment '{environmentId}'."));

    public async Task<byte[]> LogFileAsync(string environmentId, string fileName, bool download, TimeSpan? timeout, CancellationToken ct) =>
        (await DeployApi.Map(() => client.SendCheckedAsync(HttpMethod.Get, $"/api/logs/v2/{DeployApi.Seg(environmentId)}/{DeployApi.Seg(fileName)}?download={(download ? "True" : "False")}", null, ct, timeout),
            (HttpStatusCode.NotFound, $"Log file '{fileName}' was not found for environment '{environmentId}'."))).Body;

    public Task<JsonElement> DeploymentLogsAsync(string deploymentId, CancellationToken ct) =>
        DeployApi.Map(() => client.GetJsonAsync($"/api/Deployments/v1/{DeployApi.Seg(deploymentId)}", ct),
            (HttpStatusCode.NotFound, $"No logs were found for deployment '{deploymentId}'."));
}
