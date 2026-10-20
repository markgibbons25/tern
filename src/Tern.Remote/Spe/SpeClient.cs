using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tern.Remote.Spe;

public sealed class SpeException(string message) : Exception(message);

/// <summary>How to authenticate to Sitecore PowerShell Extensions remoting.</summary>
public abstract record SpeCredentials(string UserName)
{
    /// <summary>HTTP Basic with a Sitecore user name and password.</summary>
    public sealed record Basic(string UserName, string Password) : SpeCredentials(UserName);

    /// <summary>A short-lived token signed with the shared secret SPE is configured with.</summary>
    public sealed record SharedSecret(string UserName, string Secret) : SpeCredentials(UserName);
}

/// <summary>
/// Runs a script through SPE remoting (<c>/-/script/script/</c>) and returns what it wrote. This
/// is separate from Management Services: SPE must be installed with remoting enabled, which is
/// usual on a Local CM and rare on a SitecoreAI environment.
/// </summary>
public sealed class SpeClient(HttpClient http, string host, SpeCredentials credentials)
{
    public async Task<string> RunAsync(string script, CancellationToken ct)
    {
        var session = Guid.NewGuid().ToString("D");
        var url = $"{host.TrimEnd('/')}/-/script/script/?sessionId={session}&rawOutput=True&persistentSession=False";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent($"{script}\r\n <#{session}#>\r\n", Encoding.UTF8, "text/plain"),
        };
        request.Headers.Authorization = credentials switch
        {
            SpeCredentials.Basic b => new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{b.UserName}:{b.Password}"))),
            SpeCredentials.SharedSecret s => new AuthenticationHeaderValue("Bearer", Token(s, host)),
            _ => throw new ArgumentOutOfRangeException(nameof(credentials)),
        };
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var hint = (int)response.StatusCode switch
            {
                404 => " SPE remoting is not enabled, or SPE is not installed on this CM.",
                401 or 403 => " The CM refused the SPE credentials, or remoting is not allowed for this user.",
                _ => string.Empty,
            };
            throw new SpeException($"SPE remoting returned {(int)response.StatusCode}.{hint} {Truncate(body)}".Trim());
        }
        return body;
    }

    /// <summary>An HS256 token in the shape SPE's shared-secret authentication accepts.</summary>
    internal static string Token(SpeCredentials.SharedSecret credentials, string host, DateTimeOffset? now = null)
    {
        var issued = now ?? DateTimeOffset.UtcNow;
        static string B64(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string Json(Action<Utf8JsonWriter> write)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                write(w);
                w.WriteEndObject();
            }
            return B64(stream.ToArray());
        }
        var header = Json(w =>
        {
            w.WriteString("alg", "HS256");
            w.WriteString("typ", "JWT");
        });
        var payload = Json(w =>
        {
            w.WriteString("iss", "SPE Remoting");
            w.WriteString("aud", host.TrimEnd('/'));
            w.WriteString("name", credentials.UserName);
            w.WriteNumber("exp", issued.AddSeconds(30).ToUnixTimeSeconds());
        });
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(credentials.Secret), Encoding.UTF8.GetBytes($"{header}.{payload}"));
        return $"{header}.{payload}.{B64(signature)}";
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
