using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tern.Remote.Auth;
using Tern.Remote.Management;

namespace Tern.Remote.Authoring;

/// <summary>
/// The Authoring GraphQL API at <c>/sitecore/api/authoring/graphql/v1/</c>. Unlike Management
/// Services it does not need a Sitecore Administrator, so it suits everyday content changes.
/// </summary>
public sealed class AuthoringClient(HttpClient http, string host, TokenProvider tokens)
{
    public const string ServicePath = "/sitecore/api/authoring/graphql/v1/";

    /// <summary>Sends a GraphQL document and returns the whole response, errors included.</summary>
    public async Task<JsonDocument> QueryAsync(string query, string? variablesJson, CancellationToken ct)
    {
        var body = Body(query, variablesJson);
        var token = await tokens.GetTokenAsync(ct);
        var (status, text) = await SendAsync(body, token, ct);
        if (status is 401 or 403)
        {
            token = await tokens.ForceRefreshAsync(token, ct);
            (status, text) = await SendAsync(body, token, ct);
        }
        if (status >= 400)
        {
            throw new ManagementException($"{host}{ServicePath} returned {status}: {(text.Length > 500 ? text[..500] : text)}");
        }
        return JsonDocument.Parse(text);
    }

    /// <summary>Sets fields on one item version through <c>updateItem</c>. A null value resets the field.</summary>
    public async Task<JsonDocument> UpdateItemAsync(string pathOrId, string database, string? language, int? version, IReadOnlyList<(string Name, string? Value)> fields, CancellationToken ct)
    {
        const string mutation = "mutation($input: UpdateItemInput!) { updateItem(input: $input) { item { itemId path name version language { name } } } }";
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartObject("input");
            if (Guid.TryParse(pathOrId, out var id))
            {
                w.WriteString("itemId", id);
            }
            else
            {
                w.WriteString("path", pathOrId);
            }
            w.WriteString("database", database);
            if (language is not null)
            {
                w.WriteString("language", language);
            }
            if (version is { } v)
            {
                w.WriteNumber("version", v);
            }
            w.WriteStartArray("fields");
            foreach (var (name, value) in fields)
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                if (value is null)
                {
                    w.WriteBoolean("reset", true);
                }
                else
                {
                    w.WriteString("value", value);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        var doc = await QueryAsync(mutation, Encoding.UTF8.GetString(stream.ToArray()), ct);
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var messages = string.Join("; ", errors.EnumerateArray().Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : e.GetRawText()));
            doc.Dispose();
            throw new ManagementException("The authoring API rejected the change: " + messages);
        }
        return doc;
    }

    private async Task<(int Status, string Body)> SendAsync(byte[] body, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, host.TrimEnd('/') + ServicePath) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static byte[] Body(string query, string? variablesJson)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("query", query);
            if (!string.IsNullOrWhiteSpace(variablesJson))
            {
                using var vars = JsonDocument.Parse(variablesJson);
                w.WritePropertyName("variables");
                vars.RootElement.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return stream.ToArray();
    }
}
