using Tern.Remote.Auth;
using Tern.Remote.Cloud;
using Tern.Remote.Login;

namespace Tern.Cli.Common;

/// <summary>An authenticated connection to SitecoreAI's Deploy and Monitoring APIs, from the stored <c>XmCloud</c> login.</summary>
public sealed class CloudContext
{
    private CloudContext(Endpoint endpoint, TokenProvider tokens, DeployApi deploy, MonitoringApi monitoring)
    {
        Endpoint = endpoint;
        Tokens = tokens;
        Deploy = deploy;
        Monitoring = monitoring;
    }

    public Endpoint Endpoint { get; }
    public TokenProvider Tokens { get; }
    public DeployApi Deploy { get; }
    public MonitoringApi Monitoring { get; }

    public static CloudContext Open(Session session)
    {
        var endpoint = CloudEndpoints.Find(session.Store);
        var environment = EnvironmentCredentials.For(CloudEndpoints.Name, session.EnvironmentVariables);
        TokenProvider tokens;
        if (endpoint is not null && (endpoint.AccessToken is not null || environment is not null))
        {
            tokens = session.Tokens(endpoint);
        }
        else if (environment is not null)
        {
            // Client credentials from the environment work without any stored login, and nothing is written.
            endpoint = LoginStore.Load(Path.Join(Path.GetTempPath(), $"tern-{Guid.NewGuid():N}.json")).GetOrAdd(LoginStore.OfficialKey(CloudEndpoints.Name));
            tokens = new TokenProvider(endpoint, new OidcClient(Session.Http), environment);
        }
        else
        {
            throw new CliException($"You are not logged in to SitecoreAI ({session.StorePath} has no {CloudEndpoints.Name} endpoint with a token). Run `tern cloud login` first.");
        }
        void Debug(string message) => session.Output.Debug(message);
        var deploy = new DeployApi(new CloudClient(Session.Http, CloudEndpoints.DeployHost(endpoint), tokens, debug: Debug));
        var monitoring = new MonitoringApi(new CloudClient(Session.Http, CloudEndpoints.MonitoringHost(endpoint), tokens, debug: Debug));
        return new CloudContext(endpoint, tokens, deploy, monitoring);
    }

    /// <summary>The organization to act on: the option's value, or the one the access token belongs to.</summary>
    public async Task<string> OrganizationIdAsync(string? option, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(option))
        {
            return option;
        }
        return Jwt.Claim(await Tokens.GetTokenAsync(ct), CloudEndpoints.OrganizationClaim)
            ?? throw new CliException("The SitecoreAI login does not name an organization. Pass --organization-id.");
    }
}
