using Tern.Remote.Login;

namespace Tern.Remote.Auth;

/// <summary>
/// Supplies a bearer token for an Endpoint. Unlike the Official CLI, it refreshes a token that is
/// about to expire before using it (Deviation D3), and it can take client credentials from
/// environment variables without ever writing them to disk.
/// </summary>
public sealed class TokenProvider
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly Endpoint _endpoint;
    private readonly OidcClient _oidc;
    private readonly EnvironmentCredentials? _envCredentials;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _envToken;
    private DateTimeOffset? _envTokenExpires;

    public TokenProvider(Endpoint endpoint, OidcClient oidc, EnvironmentCredentials? envCredentials = null, Func<DateTimeOffset>? now = null)
    {
        _endpoint = endpoint;
        _oidc = oidc;
        _envCredentials = envCredentials;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised whenever a token refresh changed what is stored, so the caller can save the store.</summary>
    public event Action? StoreChanged;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_envCredentials is not null)
            {
                if (_envToken is null || _envTokenExpires is { } exp && exp - RefreshMargin <= _now())
                {
                    await FetchFromEnvironmentAsync(ct);
                }
                return _envToken!;
            }
            if (_endpoint.AccessToken is { } current && !(_endpoint.AccessTokenExpires is { } expires && expires - RefreshMargin <= _now()))
            {
                return current;
            }
            return await RefreshCoreAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Gets a new token after the CM rejected <paramref name="rejectedToken"/>.</summary>
    public async Task<string> ForceRefreshAsync(string rejectedToken, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_envCredentials is not null)
            {
                if (_envToken is null || _envToken == rejectedToken)
                {
                    await FetchFromEnvironmentAsync(ct);
                }
                return _envToken!;
            }
            // Another request may already have refreshed it.
            if (_endpoint.AccessToken is { } current && current != rejectedToken)
            {
                return current;
            }
            return await RefreshCoreAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task FetchFromEnvironmentAsync(CancellationToken ct)
    {
        var env = _envCredentials!;
        var token = await _oidc.ClientCredentialsAsync(env.Authority ?? _endpoint.Authority ?? Defaults.Authority, env.ClientId, env.ClientSecret, env.Audience ?? _endpoint.Audience ?? Defaults.Audience, _endpoint.Insecure, ct);
        _envToken = token.AccessToken;
        _envTokenExpires = token.ExpiresIn is { } s ? _now().AddSeconds(s) : null;
    }

    private async Task<string> RefreshCoreAsync(CancellationToken ct)
    {
        var authority = _endpoint.Authority ?? throw new AuthenticationException($"Endpoint '{_endpoint.Name}' has no authority. Run `tern login` first.");
        TokenResponse token;
        if (_endpoint.UseClientCredentials && _endpoint.ClientId is { } id && _endpoint.ClientSecret is { } secret)
        {
            token = await _oidc.ClientCredentialsAsync(authority, id, secret, _endpoint.Audience, _endpoint.Insecure, ct);
        }
        else if (_endpoint.RefreshToken is { } refresh && _endpoint.ClientId is { } clientId)
        {
            token = await _oidc.RefreshAsync(authority, clientId, refresh, _endpoint.RefreshTokenParameters, _endpoint.Insecure, ct);
        }
        else
        {
            throw new AuthenticationException($"The login for endpoint '{_endpoint.Name}' has expired and cannot be refreshed. Run `tern login` again.");
        }
        _endpoint.AccessToken = token.AccessToken;
        if (token.RefreshToken is not null)
        {
            _endpoint.RefreshToken = token.RefreshToken;
        }
        _endpoint.ExpiresIn = token.ExpiresIn;
        _endpoint.LastUpdated = _now();
        StoreChanged?.Invoke();
        return token.AccessToken;
    }
}

/// <summary>Client credentials taken from <c>TERN_*</c> environment variables.</summary>
public sealed record EnvironmentCredentials(string ClientId, string ClientSecret, string? Authority, string? Audience)
{
    /// <summary>
    /// Reads <c>TERN_&lt;ENDPOINT&gt;_CLIENT_ID</c>/<c>_CLIENT_SECRET</c>, falling back to
    /// <c>TERN_CLIENT_ID</c>/<c>TERN_CLIENT_SECRET</c>. Authority and audience follow the same pattern.
    /// </summary>
    public static EnvironmentCredentials? For(string endpointName, Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var prefix = "TERN_" + new string(endpointName.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray()) + "_";
        string? Get(string name) => read(prefix + name) ?? read("TERN_" + name);
        var id = Get("CLIENT_ID");
        var secret = Get("CLIENT_SECRET");
        return id is null || secret is null ? null : new EnvironmentCredentials(id, secret, Get("AUTHORITY"), Get("AUDIENCE"));
    }
}

public static class Defaults
{
    public const string Authority = "https://auth.sitecorecloud.io/";
    public const string Audience = "https://api.sitecorecloud.io";

    /// <summary>SitecoreAI's public device-flow client, the one the Official CLI's <c>cloud login</c> uses.</summary>
    public const string SitecoreAiDeviceClientId = "Chi8EwfFnEejksk3Sed9hlalGiM9B2v7";

    public const string DeployApi = "https://xmclouddeploy-api.sitecorecloud.io/";
    public const string MonitoringApi = "https://xmcloud-monitoring-api.sitecorecloud.io/";
}
