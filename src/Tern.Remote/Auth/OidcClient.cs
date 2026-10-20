using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tern.Remote.Auth;

public sealed class AuthenticationException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record TokenResponse(string AccessToken, string? RefreshToken, int? ExpiresIn);

/// <summary>
/// The two OAuth flows the Official CLI uses: the device authorization grant (RFC 8628) for
/// people, and client credentials for automation. Discovery is read from the authority's
/// <c>/.well-known/openid-configuration</c>.
/// </summary>
public sealed class OidcClient(HttpClient http)
{
    private readonly Dictionary<string, JsonDocument> _discovery = new(StringComparer.OrdinalIgnoreCase);

    public async Task<TokenResponse> ClientCredentialsAsync(string authority, string clientId, string clientSecret, string? audience, bool insecure, CancellationToken ct)
    {
        var tokenEndpoint = await TokenEndpointAsync(authority, insecure, ct);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        };
        if (!string.IsNullOrEmpty(audience))
        {
            form["audience"] = audience;
        }
        return await PostTokenAsync(tokenEndpoint, form, ct);
    }

    public async Task<TokenResponse> RefreshAsync(string authority, string clientId, string refreshToken, IReadOnlyDictionary<string, string> extra, bool insecure, CancellationToken ct)
    {
        var tokenEndpoint = await TokenEndpointAsync(authority, insecure, ct);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        };
        foreach (var (k, v) in extra)
        {
            form[k] = v;
        }
        return await PostTokenAsync(tokenEndpoint, form, ct);
    }

    /// <summary>Runs the device flow: shows the code, opens a browser when it can, and polls until approved.</summary>
    public async Task<TokenResponse> DeviceCodeAsync(string authority, string clientId, string? audience, bool insecure, TextWriter prompt, bool openBrowser, CancellationToken ct)
    {
        var discovery = await DiscoverAsync(authority, insecure, ct);
        var deviceEndpoint = discovery.RootElement.TryGetProperty("device_authorization_endpoint", out var d) ? d.GetString() : null;
        if (deviceEndpoint is null)
        {
            throw new AuthenticationException($"{authority} does not support the device authorization grant.");
        }
        var form = new Dictionary<string, string> { ["client_id"] = clientId };
        if (!string.IsNullOrEmpty(audience))
        {
            form["audience"] = audience;
        }
        using var response = await http.PostAsync(deviceEndpoint, new FormUrlEncodedContent(form), ct);
        using var json = await ReadJsonAsync(response, ct);
        var root = json.RootElement;
        var deviceCode = root.GetProperty("device_code").GetString()!;
        var userCode = root.TryGetProperty("user_code", out var uc) ? uc.GetString() : null;
        var verification = root.TryGetProperty("verification_uri_complete", out var vc) ? vc.GetString()
            : root.TryGetProperty("verification_uri", out var vu) ? vu.GetString() : null;
        var interval = root.TryGetProperty("interval", out var iv) && iv.TryGetInt32(out var i) ? i : 5;

        prompt.WriteLine($"Your one-time code is {userCode}. Approve this sign-in at:");
        prompt.WriteLine($"  {verification}");
        if (openBrowser && verification is not null)
        {
            TryOpenBrowser(verification);
        }

        var tokenEndpoint = await TokenEndpointAsync(authority, insecure, ct);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var poll = new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode,
                ["client_id"] = clientId,
            };
            using var tokenResponse = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(poll), ct);
            using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
            if (tokenResponse.IsSuccessStatusCode)
            {
                return ToToken(tokenJson.RootElement);
            }
            var error = tokenJson.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                default:
                    throw new AuthenticationException($"Device sign-in failed: {error ?? tokenResponse.StatusCode.ToString()}");
            }
        }
    }

    private async Task<string> TokenEndpointAsync(string authority, bool insecure, CancellationToken ct)
    {
        var discovery = await DiscoverAsync(authority, insecure, ct);
        var endpoint = discovery.RootElement.GetProperty("token_endpoint").GetString()!;
        if (insecure)
        {
            // Docker-internal authorities advertise host names the client can't reach; keep the
            // path but use the authority's scheme, host and port, as the Official CLI does.
            var advertised = new Uri(endpoint);
            var a = new Uri(authority);
            endpoint = new UriBuilder(advertised) { Scheme = a.Scheme, Host = a.Host, Port = a.Port }.Uri.ToString();
        }
        return endpoint;
    }

    private async Task<JsonDocument> DiscoverAsync(string authority, bool insecure, CancellationToken ct)
    {
        if (_discovery.TryGetValue(authority, out var cached))
        {
            return cached;
        }
        if (!insecure && !authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthenticationException($"The authority {authority} must use HTTPS. Pass --insecure for a local identity server.");
        }
        var url = authority.TrimEnd('/') + "/.well-known/openid-configuration";
        using var response = await http.GetAsync(url, ct);
        var doc = await ReadJsonAsync(response, ct);
        _discovery[authority] = doc;
        return doc;
    }

    private async Task<TokenResponse> PostTokenAsync(string tokenEndpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct);
        using var json = await ReadJsonAsync(response, ct);
        return ToToken(json.RootElement);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = body;
            try
            {
                using var err = JsonDocument.Parse(body);
                detail = err.RootElement.TryGetProperty("error_description", out var desc) ? desc.GetString() ?? body
                    : err.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? body : body;
            }
            catch (JsonException)
            {
            }
            throw new AuthenticationException($"{response.RequestMessage?.RequestUri} returned {(int)response.StatusCode}: {detail}");
        }
        return JsonDocument.Parse(body);
    }

    private static TokenResponse ToToken(JsonElement root) => new(
        root.GetProperty("access_token").GetString()!,
        root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
        root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : null);

    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url.Replace("&", "^&")}\"") { CreateNoWindow = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch (Exception)
        {
            // Opening a browser is a convenience; the code and URL are already printed.
        }
    }
}
