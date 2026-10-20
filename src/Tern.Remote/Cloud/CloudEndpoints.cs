using Tern.Remote.Auth;
using Tern.Remote.Login;

namespace Tern.Remote.Cloud;

/// <summary>What <c>cloud login</c> stores for the SitecoreAI endpoint.</summary>
public sealed record CloudLogin(
    string Authority,
    string ClientId,
    string? ClientSecret,
    bool UseClientCredentials,
    string Audience,
    string DeployHost,
    string MonitoringHost,
    bool AllowWrite,
    TokenResponse Token);

/// <summary>
/// The SitecoreAI endpoints in the shared login store, laid out exactly as the Official CLI's
/// XMCloud plugin writes them (ADR 0004).
/// </summary>
public static class CloudEndpoints
{
    /// <summary>The endpoint <c>cloud login</c> writes. Names match ignoring case, so an existing <c>xmCloud</c> is reused.</summary>
    public const string Name = "XmCloud";

    /// <summary>The endpoint variable that holds the Monitoring API host.</summary>
    public const string MonitoringVariable = "XmCloudMonitoringHost";

    /// <summary>The access-token claim that names the user's organization.</summary>
    public const string OrganizationClaim = "https://auth.sitecorecloud.io/claims/org_id";

    public const string DeployApp = "https://deploy.sitecorecloud.io/";

    public static Endpoint? Find(LoginStore store) => store.Find(Name);

    public static Uri DeployHost(Endpoint endpoint) => new(string.IsNullOrWhiteSpace(endpoint.Host) ? Defaults.DeployApi : endpoint.Host);

    public static Uri MonitoringHost(Endpoint endpoint) =>
        new(endpoint.Variables.TryGetValue(MonitoringVariable, out var host) && !string.IsNullOrWhiteSpace(host) ? host : Defaults.MonitoringApi);

    /// <summary>Replaces the SitecoreAI endpoint with a fresh login, keeping the endpoint's key if it already exists.</summary>
    public static Endpoint StoreLogin(LoginStore store, CloudLogin login, DateTimeOffset now)
    {
        var endpoint = store.Replace(Find(store)?.Name ?? LoginStore.OfficialKey(Name));
        endpoint.AllowWrite = login.AllowWrite;
        endpoint.Host = login.DeployHost;
        endpoint.Authority = login.Authority;
        if (login.UseClientCredentials)
        {
            endpoint.UseClientCredentials = true;
        }
        endpoint.AccessToken = login.Token.AccessToken;
        if (login.Token.RefreshToken is not null)
        {
            endpoint.RefreshToken = login.Token.RefreshToken;
        }
        if (Jwt.Claim(login.Token.AccessToken, OrganizationClaim) is { } organization)
        {
            endpoint.RefreshTokenParameters = new Dictionary<string, string> { ["organization_id"] = organization };
        }
        endpoint.ExpiresIn = login.Token.ExpiresIn;
        endpoint.LastUpdated = now;
        endpoint.ClientId = login.ClientId;
        if (login.UseClientCredentials && login.ClientSecret is not null)
        {
            endpoint.ClientSecret = login.ClientSecret;
        }
        endpoint.SetVariables([new(LoginStore.OfficialKey(MonitoringVariable), login.MonitoringHost)]);
        endpoint.Audience = login.Audience;
        return endpoint;
    }

    /// <summary>Removes the SitecoreAI endpoint and every endpoint that borrows its login. Returns the names removed.</summary>
    public static IReadOnlyList<string> Logout(LoginStore store)
    {
        var cloud = Find(store);
        if (cloud is null)
        {
            return [];
        }
        var names = store.All
            .Where(e => e.Name.Equals(cloud.Name, StringComparison.OrdinalIgnoreCase) || (e.Ref is { } r && r.Equals(cloud.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.Name)
            .ToList();
        foreach (var name in names)
        {
            store.Remove(name);
        }
        return names;
    }

    /// <summary>
    /// Points an endpoint named after a SitecoreAI environment at that environment's CM, using the
    /// SitecoreAI login (<c>ref: XmCloud</c>).
    /// </summary>
    public static Endpoint Connect(LoginStore store, string environmentName, string cmHost, bool allowWrite)
    {
        var existing = store.Find(environmentName);
        var endpoint = existing ?? store.GetOrAdd(LoginStore.OfficialKey(environmentName));
        endpoint.Ref = Name;
        endpoint.AllowWrite = allowWrite;
        endpoint.Host = cmHost;
        if (existing is null)
        {
            endpoint.SetVariables([]);
        }
        return endpoint;
    }

    /// <summary>
    /// The CM URL <c>environment connect</c> stores: the <c>--cm-host</c> option when it is a full
    /// URL, otherwise <c>https://</c> and the host name, always ending in a slash.
    /// </summary>
    public static string CmHostUrl(string? cmHostOption, string environmentHost)
    {
        if (!string.IsNullOrEmpty(cmHostOption) && Uri.TryCreate(cmHostOption, UriKind.Absolute, out var absolute) && !string.IsNullOrEmpty(absolute.Host) && absolute.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return new UriBuilder(absolute).Uri.ToString();
        }
        return new UriBuilder(cmHostOption ?? environmentHost) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.ToString();
    }

    /// <summary>The page in the SitecoreAI Deploy app that shows a deployment's progress.</summary>
    public static string DeploymentPage(string deploymentId) => new Uri(new Uri(DeployApp), $"/deployment/{deploymentId}/details").AbsoluteUri;
}
