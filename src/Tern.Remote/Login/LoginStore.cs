using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tern.Remote.Login;

/// <summary>
/// The Official CLI's login store, <c>.sitecore/user.json</c>. Tern reads and writes it in the
/// same layout and keeps every property it doesn't know about exactly as it found it (ADR 0004).
/// Endpoint names match case-insensitively.
/// </summary>
public sealed class LoginStore
{
    private readonly JsonObject _root;

    private LoginStore(string path, JsonObject root)
    {
        Path = path;
        _root = root;
    }

    public string Path { get; }

    public static LoginStore Load(string path)
    {
        if (!File.Exists(path))
        {
            return new LoginStore(path, new JsonObject
            {
                ["endpoints"] = new JsonObject(),
                ["$schema"] = "./schemas/UserConfiguration.schema.json",
            });
        }
        var bytes = File.ReadAllBytes(path).AsSpan();
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bytes = bytes[3..];
        }
        var node = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return new LoginStore(path, node as JsonObject ?? throw new InvalidDataException($"{path} is not a JSON object."));
    }

    private JsonObject Endpoints
    {
        get
        {
            if (_root["endpoints"] is not JsonObject endpoints)
            {
                endpoints = new JsonObject();
                _root["endpoints"] = endpoints;
            }
            return endpoints;
        }
    }

    public string DefaultEndpointName
    {
        get => (string?)_root["defaultEndpoint"] ?? "default";
        set => _root["defaultEndpoint"] = value;
    }

    public IEnumerable<Endpoint> All => Endpoints.Select(p => new Endpoint(this, p.Key, (JsonObject)p.Value!));

    /// <summary>The named endpoint, or the default one when <paramref name="name"/> is null.</summary>
    public Endpoint? Find(string? name)
    {
        name ??= DefaultEndpointName;
        foreach (var (key, value) in Endpoints)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase) && value is JsonObject obj)
            {
                return new Endpoint(this, key, obj);
            }
        }
        return null;
    }

    public Endpoint GetOrAdd(string name)
    {
        if (Find(name) is { } existing)
        {
            return existing;
        }
        var obj = new JsonObject();
        Endpoints[name] = obj;
        return new Endpoint(this, name, obj);
    }

    public bool Remove(string name)
    {
        var key = Endpoints.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        return key is not null && Endpoints.Remove(key);
    }

    /// <summary>Writes the store back the way the Official CLI writes it: two-space indent, no BOM.</summary>
    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var options = new JsonWriterOptions { Indented = true, IndentSize = 2, NewLine = Environment.NewLine, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            _root.WriteTo(writer);
        }
        var temp = Path + ".tern-tmp";
        File.WriteAllBytes(temp, stream.ToArray());
        File.Move(temp, Path, overwrite: true);
    }

    internal static string FormatDate(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// A named, stored login. An endpoint with a <c>ref</c> keeps its own host and write permission
/// but borrows authority, client and tokens from the endpoint it refers to.
/// </summary>
public sealed class Endpoint
{
    private readonly LoginStore _store;
    private readonly JsonObject _node;

    internal Endpoint(LoginStore store, string name, JsonObject node)
    {
        _store = store;
        Name = name;
        _node = node;
    }

    public string Name { get; }

    public string? Ref { get => Str("ref"); set => Set("ref", value); }
    public bool AllowWrite { get => (bool?)_node["allowWrite"] ?? false; set => _node["allowWrite"] = value; }
    public string? Host { get => Str("host"); set => Set("host", value); }

    /// <summary>The endpoint whose credentials and tokens this one uses.</summary>
    public Endpoint AuthSource => Ref is { } r && _store.Find(r) is { } target && target.Name != Name ? target : this;

    public string? Authority { get => AuthSource.Str("authority"); set => AuthSource.Set("authority", value); }
    public string? ClientId { get => AuthSource.Str("clientId"); set => AuthSource.Set("clientId", value); }
    public string? ClientSecret { get => AuthSource.Str("clientSecret"); set => AuthSource.Set("clientSecret", value); }
    public string? Audience { get => AuthSource.Str("audience"); set => AuthSource.Set("audience", value); }
    public string? AccessToken { get => AuthSource.Str("accessToken"); set => AuthSource.Set("accessToken", value); }
    public string? RefreshToken { get => AuthSource.Str("refreshToken"); set => AuthSource.Set("refreshToken", value); }
    public bool UseClientCredentials { get => (bool?)AuthSource._node["useClientCredentials"] ?? false; set => AuthSource._node["useClientCredentials"] = value; }
    public bool Insecure { get => (bool?)AuthSource._node["insecure"] ?? false; set => AuthSource._node["insecure"] = value; }

    public int? ExpiresIn
    {
        get => (int?)AuthSource._node["expiresIn"];
        set => AuthSource._node["expiresIn"] = value;
    }

    public DateTimeOffset? LastUpdated
    {
        get => DateTimeOffset.TryParse(AuthSource.Str("lastUpdated"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        set => AuthSource.Set("lastUpdated", value is { } d ? LoginStore.FormatDate(d) : null);
    }

    public IReadOnlyDictionary<string, string> RefreshTokenParameters
    {
        get => AuthSource._node["refreshTokenParameters"] is JsonObject o
            ? o.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!.ToString())
            : new Dictionary<string, string>();
        set
        {
            var o = new JsonObject();
            foreach (var (k, v) in value)
            {
                o[k] = v;
            }
            AuthSource._node["refreshTokenParameters"] = o;
        }
    }

    public IReadOnlyDictionary<string, string> Variables =>
        _node["variables"] is JsonObject o
            ? o.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!.ToString(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public void SetVariable(string key, string value)
    {
        if (_node["variables"] is not JsonObject o)
        {
            o = new JsonObject();
            _node["variables"] = o;
        }
        o[key] = value;
    }

    /// <summary>When the access token expires, if the store records enough to tell.</summary>
    public DateTimeOffset? AccessTokenExpires => LastUpdated is { } at && ExpiresIn is { } seconds ? at.AddSeconds(seconds) : null;

    private string? Str(string key) => _node[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private void Set(string key, string? value)
    {
        if (value is null)
        {
            _node.Remove(key);
        }
        else
        {
            _node[key] = value;
        }
    }

    public override string ToString() => $"{Name} ({Host})";
}
