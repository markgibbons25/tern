using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Tern.Remote.Auth;

namespace Tern.Remote.Cloud;

/// <summary>A SitecoreAI API call that failed. The message is safe to show as is.</summary>
public sealed class CloudApiException(string message, HttpStatusCode? statusCode = null, string? body = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    /// <summary>The response body, when there was one.</summary>
    public string? Body { get; } = body;

    /// <summary>A field of the RFC 7807 problem document the API returns with most errors, if present.</summary>
    public string? Problem(string field)
    {
        if (string.IsNullOrWhiteSpace(Body))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(Body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && CloudJson.TryGet(doc.RootElement, field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What came back from a SitecoreAI API call.</summary>
public sealed record CloudResponse(HttpStatusCode Status, string? Location, byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);

    /// <summary>The last segment of the <c>Location</c> header: the ID of whatever the call created.</summary>
    public string? CreatedId => Location is { } l ? l.TrimEnd('/').Split('/').Last() : null;
}

/// <summary>
/// The HTTP transport shared by the Deploy API and the Monitoring API. Every call carries the
/// SitecoreAI endpoint's bearer token and is retried three times, waiting 2, 4 and 8 seconds,
/// after a network error, a timeout, a 401 (with a fresh token) or a 429, as the Official CLI does.
/// </summary>
public sealed class CloudClient
{
    public const int Retries = 3;

    private readonly HttpClient _http;
    private readonly TokenProvider _tokens;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _debug;

    public CloudClient(HttpClient http, Uri baseAddress, TokenProvider tokens, Func<TimeSpan, CancellationToken, Task>? delay = null, Action<string>? debug = null)
    {
        _http = http;
        BaseAddress = baseAddress;
        _tokens = tokens;
        _delay = delay ?? Task.Delay;
        _debug = debug;
    }

    public Uri BaseAddress { get; }

    public TokenProvider Tokens => _tokens;

    public Uri Resolve(string pathOrUrl) =>
        Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? absolute
            : new Uri(BaseAddress, pathOrUrl);

    /// <summary>Sends one request with retries, and returns whatever status the last attempt got.</summary>
    public async Task<CloudResponse> SendAsync(HttpMethod method, string pathOrUrl, Func<HttpContent>? content, TimeSpan? timeout, CancellationToken ct)
    {
        var uri = Resolve(pathOrUrl);
        var token = await _tokens.GetTokenAsync(ct);
        for (var attempt = 0; ; attempt++)
        {
            var wait = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
            try
            {
                using var request = new HttpRequestMessage(method, uri) { Content = content?.Invoke() };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var timeoutSource = timeout is { } t ? new CancellationTokenSource(t) : null;
                using var linked = timeoutSource is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
                _debug?.Invoke($"{method} {uri}");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked?.Token ?? ct);
                var body = await response.Content.ReadAsByteArrayAsync(ct);
                _debug?.Invoke($"{method} {uri} returned {(int)response.StatusCode}");
                if (attempt < Retries && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        token = await _tokens.ForceRefreshAsync(token, ct);
                    }
                    await _delay(wait, ct);
                    continue;
                }
                var location = response.Headers.Location?.ToString();
                return new CloudResponse(response.StatusCode, location, body);
            }
            catch (Exception ex) when (attempt < Retries && !ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or TimeoutException)
            {
                _debug?.Invoke($"{method} {uri} failed ({ex.Message}); trying again in {wait.TotalSeconds} seconds");
                await _delay(wait, ct);
            }
        }
    }

    public async Task<CloudResponse> SendCheckedAsync(HttpMethod method, string pathOrUrl, Func<HttpContent>? content, CancellationToken ct, TimeSpan? timeout = null)
    {
        var response = await SendAsync(method, pathOrUrl, content, timeout, ct);
        EnsureSuccess(method, Resolve(pathOrUrl), response);
        return response;
    }

    public async Task<JsonElement> GetJsonAsync(string pathOrUrl, CancellationToken ct)
    {
        var response = await SendCheckedAsync(HttpMethod.Get, pathOrUrl, null, ct);
        return Parse(response, Resolve(pathOrUrl));
    }

    public Task<CloudResponse> PostJsonAsync(string path, Action<Utf8JsonWriter> body, CancellationToken ct) =>
        SendCheckedAsync(HttpMethod.Post, path, CloudJson.Content(body), ct);

    public Task<CloudResponse> PutJsonAsync(string path, Action<Utf8JsonWriter> body, CancellationToken ct) =>
        SendCheckedAsync(HttpMethod.Put, path, CloudJson.Content(body), ct);

    public Task<CloudResponse> PostAsync(string path, CancellationToken ct) => SendCheckedAsync(HttpMethod.Post, path, null, ct);

    public Task<CloudResponse> DeleteAsync(string path, CancellationToken ct) => SendCheckedAsync(HttpMethod.Delete, path, null, ct);

    internal static JsonElement Parse(CloudResponse response, Uri uri)
    {
        var text = response.Text;
        if (text.Contains("You need to enable JavaScript to run this app.", StringComparison.Ordinal))
        {
            throw new CloudApiException($"{uri.GetLeftPart(UriPartial.Authority)} answered with a web page, not the API. Check the host of the SitecoreAI endpoint in user.json, or run `tern cloud login` again.");
        }
        try
        {
            using var doc = JsonDocument.Parse(response.Body.Length == 0 ? "null"u8.ToArray() : response.Body);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new CloudApiException($"{uri} returned something that is not JSON: {Truncate(text)}", response.Status, text, ex);
        }
    }

    internal static void EnsureSuccess(HttpMethod method, Uri uri, CloudResponse response)
    {
        var code = (int)response.Status;
        if (code is >= 200 and < 300)
        {
            return;
        }
        var text = response.Text;
        switch (response.Status)
        {
            case HttpStatusCode.Unauthorized:
                throw new CloudApiException("SitecoreAI did not accept the login (401 Unauthorized). Run `tern cloud login` again.", response.Status, text);
            case HttpStatusCode.Forbidden:
                throw new CloudApiException("SitecoreAI refused the operation (403 Forbidden). The login has no access to it.", response.Status, text);
            case HttpStatusCode.BadRequest when ValidationErrors(text) is { } errors:
                throw new CloudApiException(errors, response.Status, text);
        }
        var ex = new CloudApiException($"{method} {uri} returned {code}.", response.Status, text);
        var detail = ex.Problem("detail") ?? ex.Problem("title");
        throw new CloudApiException(detail is null ? $"{method} {uri} returned {code}: {Truncate(text)}" : $"{method} {uri} returned {code}: {detail}", response.Status, text);
    }

    /// <summary>The messages of an ASP.NET validation problem (<c>errors: {field: [messages]}</c>), joined.</summary>
    private static string? ValidationErrors(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !CloudJson.TryGet(doc.RootElement, "errors", out var errors) || errors.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var messages = errors.EnumerateObject()
                .SelectMany(p => p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().Select(m => m.ToString()) : [p.Value.ToString()])
                .ToList();
            return messages.Count == 0 ? null : string.Join("; ", messages);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}

/// <summary>Small helpers for reading and writing the SitecoreAI APIs' JSON without reflection.</summary>
public static class CloudJson
{
    public static Func<HttpContent> Content(Action<Utf8JsonWriter> write)
    {
        var bytes = Write(write);
        return () =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            return content;
        };
    }

    public static byte[] Write(Action<Utf8JsonWriter> write, bool indented = false)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Finds a property ignoring case, the way the Official CLI's deserializer matches them.</summary>
    public static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty(name, out value))
            {
                return true;
            }
            foreach (var p in obj.EnumerateObject())
            {
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    public static string? String(JsonElement obj, string name) =>
        TryGet(obj, name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.ToString(),
        } : null;

    public static IEnumerable<JsonElement> Items(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    /// <summary>
    /// Joins a <c>location</c> property and a response body into one object, as the Official CLI
    /// does for the responses of create calls. Properties in the body win.
    /// </summary>
    public static JsonElement WithLocation(CloudResponse response)
    {
        JsonElement? body = null;
        if (response.Body.Length > 0)
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                body = doc.RootElement.Clone();
            }
        }
        var bytes = Write(w =>
        {
            if (!(body is { } b && TryGet(b, "location", out _)))
            {
                w.WriteString("location", response.Location);
            }
            if (body is { } o)
            {
                foreach (var p in o.EnumerateObject())
                {
                    p.WriteTo(w);
                }
            }
        });
        using var merged = JsonDocument.Parse(bytes);
        return merged.RootElement.Clone();
    }
}
