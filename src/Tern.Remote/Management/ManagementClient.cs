using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tern.Remote.Auth;

namespace Tern.Remote.Management;

public sealed class ManagementException(string message, Exception? inner = null, int? statusCode = null) : Exception(message, inner)
{
    /// <summary>The HTTP status the CM answered with, when the failure was one.</summary>
    public int? StatusCode { get; } = statusCode;
}

/// <summary>
/// Sends GraphQL to a CM's Management Services endpoint, <c>/sitecore/api/management</c>. It
/// opts out of the server's version comparison (ADR 0006), and on a 401, 403 or 302 it gets a
/// new token and retries once.
/// </summary>
public sealed class ManagementClient
{
    public const string ServicePath = "/sitecore/api/management";

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly TokenProvider _tokens;
    private readonly RequestRecorder? _recorder;

    public ManagementClient(HttpClient http, string host, TokenProvider tokens, RequestRecorder? recorder = null)
    {
        _http = http;
        _endpoint = new Uri(host.TrimEnd('/') + ServicePath);
        _tokens = tokens;
        _recorder = recorder;
        Host = host.TrimEnd('/');
    }

    public string Host { get; }

    /// <summary>Requests sent so far; the benchmarks report it.</summary>
    public int RequestCount => _requestCount;

    private int _requestCount;

    public async Task<JsonDocument> QueryAsync(string query, Action<Utf8JsonWriter>? writeVariables, CancellationToken ct)
    {
        var body = BuildBody(query, writeVariables);
        var token = await _tokens.GetTokenAsync(ct);
        var (status, bytes) = await SendAsync(body, token, ct);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Found)
        {
            token = await _tokens.ForceRefreshAsync(token, ct);
            (status, bytes) = await SendAsync(body, token, ct);
        }
        // Responses are parsed from their UTF-8 bytes; text is decoded only for an error message.
        string Text() => Encoding.UTF8.GetString(bytes);
        if (status is HttpStatusCode.Found or HttpStatusCode.NotFound)
        {
            throw new ManagementException($"{_endpoint} returned {(int)status}. Make sure the GraphQL service (Sitecore Management Services) is installed on the CM.");
        }
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ManagementException($"{_endpoint} refused the login ({(int)status}). Management Services requires a Sitecore Administrator; run `tern login` again or check the account's role.");
        }
        if ((int)status >= 400)
        {
            throw new ManagementException($"{_endpoint} returned {(int)status}: {Truncate(Text())}", statusCode: (int)status);
        }
        JsonDocument doc;
        try
        {
            var json = bytes.AsMemory();
            if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                json = json[3..];
            }
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ManagementException($"{_endpoint} returned something that is not JSON: {Truncate(Text())}", ex);
        }
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            // Read before the document is disposed.
            var messages = errors.EnumerateArray().Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : e.GetRawText()).ToList();
            doc.Dispose();
            throw new ManagementException("GraphQL error: " + string.Join("; ", messages));
        }
        return doc;
    }

    private async Task<(HttpStatusCode Status, byte[] Body)> SendAsync(byte[] body, string token, CancellationToken ct)
    {
        Interlocked.Increment(ref _requestCount);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("sitecore-graphql-version-comparing-enabled", "False");
        var watch = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        _recorder?.Record(_endpoint, body, (int)response.StatusCode, Encoding.UTF8.GetString(bytes), watch.Elapsed);
        return (response.StatusCode, bytes);
    }

    private static byte[] BuildBody(string query, Action<Utf8JsonWriter>? writeVariables)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("query", query);
            if (writeVariables is not null)
            {
                w.WritePropertyName("variables");
                w.WriteStartObject();
                writeVariables(w);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}

/// <summary>
/// Writes each request and response to a folder for <c>--record</c>, with bearer tokens, secrets
/// and blob contents removed before anything reaches disk.
/// </summary>
public sealed class RequestRecorder(string directory)
{
    private int _sequence;

    public void Record(Uri endpoint, byte[] requestBody, int status, string responseBody, TimeSpan elapsed)
    {
        Directory.CreateDirectory(directory);
        var n = Interlocked.Increment(ref _sequence);
        var sb = new StringBuilder();
        sb.AppendLine($"POST {endpoint}");
        sb.AppendLine($"status: {status}  elapsed: {elapsed.TotalMilliseconds:F0} ms");
        sb.AppendLine("--- request");
        sb.AppendLine(Redact(Encoding.UTF8.GetString(requestBody)));
        sb.AppendLine("--- response");
        sb.AppendLine(Redact(responseBody));
        File.WriteAllText(Path.Combine(directory, $"{n:D5}.txt"), sb.ToString());
    }

    private static string Redact(string json)
    {
        json = System.Text.RegularExpressions.Regex.Replace(json, "(\"(?:access_token|refresh_token|client_secret|clientSecret|accessToken|refreshToken|password)\"\\s*:\\s*)\"[^\"]*\"", "$1\"<redacted>\"");
        // Blob values are base64 payloads; keep their length, not their content.
        return System.Text.RegularExpressions.Regex.Replace(json, "(\"blobId\"\\s*:\\s*\"[^\"]*\"[^}]*?\"value\"\\s*:\\s*)\"([^\"]{64,})\"|(\"value\"\\s*:\\s*)\"([^\"]{64,})\"(?=[^}]*\"blobId\")",
            m => m.Groups[1].Success ? $"{m.Groups[1].Value}\"<{m.Groups[2].Value.Length} chars>\"" : $"{m.Groups[3].Value}\"<{m.Groups[4].Value.Length} chars>\"");
    }
}
