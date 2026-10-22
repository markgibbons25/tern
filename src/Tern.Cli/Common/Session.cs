using System.Net;

using Tern.Core.Configuration;
using Tern.Core.Trees;
using Tern.Remote.Auth;
using Tern.Remote.Login;
using Tern.Remote.Management;

namespace Tern.Cli.Common;

/// <summary>
/// What a command needs to run: <c>sitecore.json</c>, its Modules, the login store and, on demand,
/// an authenticated connection to an Endpoint's CM.
/// </summary>
public sealed class Session : IDisposable
{
    private static readonly Lazy<HttpClient> SharedHttp = new(CreateHttp);
    private RootConfiguration? _root;
    private IReadOnlyList<Module>? _modules;
    private LoginStore? _store;

    public Session(string? configDirectory, Output output)
    {
        ConfigDirectory = configDirectory ?? Directory.GetCurrentDirectory();
        Output = output;
    }

    public string ConfigDirectory { get; }
    public Output Output { get; }
    public string? RecordDirectory { get; init; }
    public TernSettings Settings => _settings ??= TernSettings.Load(RootOrNull?.RootDirectory);
    private TernSettings? _settings;

    public static HttpClient Http => SharedHttp.Value;

    public RootConfiguration Root => _root ??= LoadRoot();

    /// <summary>The folder of <c>sitecore.json</c>, or the config folder when there is none.</summary>
    public string ProjectDirectory => RootOrNull?.RootDirectory ?? ConfigDirectory;

    /// <summary>Environment variables, with the <c>TERN_*</c> lines of the project folder's <c>.env</c> as a fallback.</summary>
    public Func<string, string?> EnvironmentVariables => _environmentVariables ??= EnvironmentCredentials.Reader(ProjectDirectory);
    private Func<string, string?>? _environmentVariables;

    private RootConfiguration? RootOrNull
    {
        get
        {
            try
            {
                return Root;
            }
            catch (CliException)
            {
                return null;
            }
        }
    }

    public IReadOnlyList<Module> AllModules => _modules ??= ConfigurationLoader.LoadModules(Root, Output.Warn);

    public IReadOnlyList<Module> SelectModules(IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        var selected = ConfigurationLoader.Select(AllModules, include, exclude, Output.Warn);
        if (include.Count > 0 || exclude.Count > 0)
        {
            Output.Info("Matched module configurations: " + string.Join(", ", selected.Select(m => m.Namespace)));
        }
        return selected;
    }

    /// <summary>The union of every selected Module's excluded fields and the root's, as the Official CLI applies them.</summary>
    public IReadOnlyList<Guid> ExcludedFields(IReadOnlyList<Module> modules) =>
        Root.ExcludedFields.Concat(modules.SelectMany(m => m.ExcludedFields)).Select(f => f.FieldId).Distinct().ToList();

    /// <summary>
    /// The user's Item file index for reading files with <paramref name="excluded"/> fields left
    /// out, or null when the <c>fileIndex</c> setting is off. Signatures depend on the excluded
    /// fields, so each set has its own index.
    /// </summary>
    public FileIndexCache? FileIndex(IEnumerable<Guid> excluded) =>
        Settings.FileIndex ? FileIndexCache.ForUser("excluded:" + string.Join(",", excluded.Select(g => g.ToString("N")).Order(StringComparer.Ordinal))) : null;

    public LoginStore Store => _store ??= LoadStore();

    private LoginStore LoadStore()
    {
        var store = LoginStore.Load(StorePath);
        store.Share(ProjectEndpoints.Load(ProjectDirectory));
        return store;
    }

    /// <summary>Uses another session's login store, e.g. when installing a package from a temporary folder.</summary>
    public void UseStore(LoginStore store) => _store = store;

    public string StorePath => RootOrNull?.UserConfigurationPath ?? Path.Combine(ConfigDirectory, ".sitecore", "user.json");

    public Endpoint Endpoint(string? name)
    {
        var endpoint = Store.Find(name);
        if (endpoint is null)
        {
            var label = name ?? Store.DefaultEndpointName;
            throw new CliException($"There is no endpoint named '{label}' in {StorePath}. Run `tern login` or `tern connect` first.");
        }
        if (string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new CliException($"Endpoint '{endpoint.Name}' has no host.");
        }
        return endpoint;
    }

    public TokenProvider Tokens(Endpoint endpoint)
    {
        var provider = new TokenProvider(endpoint, new OidcClient(Http), EnvironmentCredentials.For(endpoint.Name, EnvironmentVariables));
        provider.StoreChanged += () =>
        {
            lock (Store)
            {
                Store.Save();
            }
        };
        return provider;
    }

    public ManagementClient Management(Endpoint endpoint) =>
        new(Http, endpoint.Host!, Tokens(endpoint), RecordDirectory is null ? null : new RequestRecorder(RecordDirectory));

    /// <summary>Plain REST calls to the Endpoint's CM, such as the transfer APIs.</summary>
    public Tern.Remote.Transfer.CmRestClient Rest(Endpoint endpoint) => new(Http, endpoint.Host!, Tokens(endpoint));

    public SerializationApi Serialization(Endpoint endpoint) =>
        new(Management(endpoint), new SerializationApiOptions
        {
            AliasesPerRequest = Settings.AliasesPerRequest,
            ParallelRequests = Settings.ParallelRequests,
            SplitThreshold = Settings.SplitThreshold ?? DefaultSplitThreshold(endpoint.Host!),
        });

    /// <summary>
    /// A remote CM reads a large subtree about 10% faster as parallel requests (measured on SitecoreAI);
    /// a local container gains nothing, so it gets one request per subtree.
    /// </summary>
    internal static int DefaultSplitThreshold(string host)
    {
        var name = Uri.TryCreate(host, UriKind.Absolute, out var uri) ? uri.Host : host;
        var local = name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(name.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        return local ? 0 : 200;
    }

    private RootConfiguration LoadRoot()
    {
        var file = ConfigurationLoader.FindRootFile(ConfigDirectory)
            ?? throw new CliException($"No sitecore.json was found in {ConfigDirectory} or any folder above it.");
        return ConfigurationLoader.LoadRoot(file);
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = false,
            UseCookies = false,
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    public void Dispose()
    {
    }
}
