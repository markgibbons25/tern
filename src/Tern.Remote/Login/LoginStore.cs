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
    private readonly Dictionary<string, SharedEndpoint> _shared = new(StringComparer.OrdinalIgnoreCase);

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
        if (bytes.StartsWith("﻿"u8))
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

    public IEnumerable<Endpoint> All =>
        Endpoints.Select(p => new Endpoint(this, p.Key, (JsonObject)p.Value!))
            .Concat(_shared.Values.Where(s => !Stored(s.Name)).Select(s => new Endpoint(this, s.Name, new JsonObject())));

    /// <summary>
    /// Layers endpoint definitions from the project's <c>tern.json</c> under this store (ADR 0013).
    /// They are never saved: <c>user.json</c> gets an entry only when a login writes to it.
    /// </summary>
    public void Share(IEnumerable<SharedEndpoint> endpoints)
    {
        foreach (var e in endpoints)
        {
            _shared[e.Name] = e;
        }
    }

    internal SharedEndpoint? Shared(string name) => _shared.GetValueOrDefault(name);

    private bool Stored(string name) => Endpoints.Any(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds an endpoint that so far existed only in <c>tern.json</c> to the store, on its first write.</summary>
    internal void Attach(string name, JsonObject node)
    {
        if (!Stored(name))
        {
            Endpoints[name] = node;
        }
    }

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
        return _shared.GetValueOrDefault(name) is { } shared ? new Endpoint(this, shared.Name, new JsonObject()) : null;
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

    /// <summary>
    /// Empties the named endpoint, keeping its key and position, or adds an empty one. The Official
    /// CLI's <c>cloud login</c> replaces its endpoint wholesale this way.
    /// </summary>
    public Endpoint Replace(string name)
    {
        var key = Endpoints.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
        var obj = new JsonObject();
        Endpoints[key] = obj;
        return new Endpoint(this, key, obj);
    }

    /// <summary>
    /// The key the Official CLI writes for an endpoint name. Its serializer camel-cases dictionary
    /// keys, so <c>XmCloud</c> is stored as <c>xmCloud</c> and <c>DEV</c> as <c>dev</c>.
    /// </summary>
    public static string OfficialKey(string name)
    {
        if (name.Length == 0 || !char.IsUpper(name[0]))
        {
            return name;
        }
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
            {
                break;
            }
            var nextIsLower = i + 1 < chars.Length && !char.IsUpper(chars[i + 1]);
            if (i > 0 && nextIsLower)
            {
                // The last capital before a lower-case letter starts the next word, so it stays,
                // unless a separator follows it.
                if (char.IsSeparator(chars[i + 1]))
                {
                    chars[i] = char.ToLowerInvariant(chars[i]);
                }
                break;
            }
            chars[i] = char.ToLowerInvariant(chars[i]);
        }
        return new string(chars);
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

    /// <summary>The definition from the project's <c>tern.json</c>, if it has one for this name.</summary>
    public SharedEndpoint? Shared => _store.Shared(Name);

    public string? Ref { get => Str("ref"); set => Set("ref", value); }

    /// <summary>
    /// The stricter of <c>tern.json</c> and <c>user.json</c>: a shared endpoint marked read-only stays
    /// read-only whatever a local login says.
    /// </summary>
    public bool AllowWrite { get => Shared?.AllowWrite != false && ((bool?)_node["allowWrite"] ?? Shared?.AllowWrite ?? false); set => Writable["allowWrite"] = value; }

    /// <summary>Host, authority, audience and client-credentials mode come from <c>tern.json</c> when it defines them.</summary>
    public string? Host { get => Shared?.Host ?? Str("host"); set => Set("host", value); }

    /// <summary>The endpoint whose credentials and tokens this one uses.</summary>
    public Endpoint AuthSource => Ref is { } r && _store.Find(r) is { } target && target.Name != Name ? target : this;

    public string? Authority { get => AuthSource.Shared?.Authority ?? AuthSource.Str("authority"); set => AuthSource.Set("authority", value); }
    public string? ClientId { get => AuthSource.Str("clientId"); set => AuthSource.Set("clientId", value); }
    public string? ClientSecret { get => AuthSource.Str("clientSecret"); set => AuthSource.Set("clientSecret", value); }
    public string? Audience { get => AuthSource.Shared?.Audience ?? AuthSource.Str("audience"); set => AuthSource.Set("audience", value); }
    public string? AccessToken { get => AuthSource.Str("accessToken"); set => AuthSource.Set("accessToken", value); }
    public string? RefreshToken { get => AuthSource.Str("refreshToken"); set => AuthSource.Set("refreshToken", value); }
    public bool UseClientCredentials { get => AuthSource.Shared?.UseClientCredentials ?? (bool?)AuthSource._node["useClientCredentials"] ?? false; set => AuthSource.Writable["useClientCredentials"] = value; }
    public bool Insecure { get => (bool?)AuthSource._node["insecure"] ?? false; set => AuthSource.Writable["insecure"] = value; }

    public int? ExpiresIn
    {
        get => (int?)AuthSource._node["expiresIn"];
        set => AuthSource.Writable["expiresIn"] = value;
    }

    /// <summary>This endpoint's node, added to the store first if it so far existed only in <c>tern.json</c>.</summary>
    private JsonObject Writable
    {
        get
        {
            _store.Attach(Name, _node);
            return _node;
        }
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
            AuthSource.Writable["refreshTokenParameters"] = o;
        }
    }

    public IReadOnlyDictionary<string, string> Variables =>
        _node["variables"] is JsonObject o
            ? o.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!.ToString(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sets a property on this endpoint itself, even when it has a <c>ref</c>. The Official CLI's
    /// <c>cloud environment connect</c> writes client credentials this way; they are not used while
    /// the <c>ref</c> is in place.
    /// </summary>
    public void SetOwn(string key, string? value) => Set(key, value);

    /// <summary>Replaces this endpoint's own variables, writing an empty object for none.</summary>
    public void SetVariables(IEnumerable<KeyValuePair<string, string>> variables)
    {
        var o = new JsonObject();
        foreach (var (k, v) in variables)
        {
            o[k] = v;
        }
        Writable["variables"] = o;
    }

    public void SetVariable(string key, string value)
    {
        if (Writable["variables"] is not JsonObject o)
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
            Writable[key] = value;
        }
    }

    public override string ToString() => $"{Name} ({Host})";
}

/// <summary>
/// An endpoint defined in a project's committed <c>tern.json</c>: where it is and whether it may be
/// written to, never a secret or a token.
/// </summary>
public sealed record SharedEndpoint(string Name, string Host, string? Authority, string? Audience, bool? AllowWrite, bool? UseClientCredentials);
